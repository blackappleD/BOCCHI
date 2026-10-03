using System.Numerics;
using BOCCHI.Common.Config;
using BOCCHI.Common.Data.Aethernet;
using BOCCHI.Common.Services;
using BOCCHI.Treasure.ChainRecipes;
using BOCCHI.Treasure.Data;
using BOCCHI.Treasure.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using Ocelot.Extensions;
using Ocelot.Ipc.VNavmesh;
using Ocelot.Services.Logger;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace BOCCHI.Automator.Services;

/// <summary>
///     Out-of-combat movement inside the Forked Tower: open nearby coffers, walk with the party (or the
///     biggest group of players), and take the teleport sigil the group just took. In combat it lets
///     BossMod move.
/// </summary>
public sealed unsafe class ForkedTowerNavigator(
    IObjectTable objects,
    IPartyList party,
    ICondition conditions,
    IDataManager data,
    IVNavmeshIpc vnav,
    ForkedTowerConfig config,
    ILogger<ForkedTowerNavigator> logger
)
{
    // Teleport-type sigils only (EObjName). Exits to base camp (2015210, 2015214), the passage back to
    // the tower entrance (2015211) and barrier/control sigils are deliberately left out.
    private static readonly HashSet<uint> TeleportSigilIds =
    [
        // Blood
        2014557, 2014558, 2014559, 2014560, 2014561,
        // Magic
        2015189, 2015190, 2015191, 2015193, 2015195, 2015196, 2015197, 2015198, 2015199, 2015200, 2015201,
        2015202, 2015203, 2015216, 2015217, 2015218, 2015222, 2015223, 2015224, 2015225, 2015226,
    ];

    private const float FollowStartDistance = 8f;

    private const float FollowStopDistance = 4f;

    private const float CrowdRadius = 20f;

    private const int MinCrowdSize = 3;

    private const float CofferSearchRadius = 30f;

    private const float CofferMaxHeightDifference = 6f;

    private const float InteractDistance = 2f;

    private const float SigilNearCrowdRadius = 15f;

    private const float CrowdJumpDistance = 50f;

    private const float TeleportJumpDistance = 30f;

    private static readonly TimeSpan DecisionInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan RepathInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan CofferGiveUp = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan SigilGiveUp = TimeSpan.FromSeconds(25);

    private static readonly TimeSpan StatusLogInterval = TimeSpan.FromSeconds(10);

    private enum Activity
    {
        Idle,
        Coffer,
        Follow,
        Sigil,
    }

    private readonly Dictionary<ulong, DateTime> skipUntil = [];

    private Activity activity = Activity.Idle;

    private ulong activityObjectId;

    private DateTime activityStartedAt;

    private Vector3? lastDestination;

    private DateTime lastDestinationAt;

    private int shortStops;

    private DateTime nextDecisionAt;

    private DateTime nextStatusLogAt;

    private Vector3? lastPlayerPosition;

    private Vector3? lastCrowdAnchor;

    private DateTime lastCrowdAt;

    private Vector3? sigilSearchCenter;

    private DateTime sigilSearchUntil;

    private DateTime confirmYesnoUntil;

    private bool warnedVnavMissing;

    public void Tick()
    {
        if (!config.NavigateInsideTower && !config.OpenCoffersInsideTower)
        {
            Stop();
            return;
        }

        IPlayerCharacter? me = objects.LocalPlayer;
        if (me == null || me.IsDead)
        {
            Stop();
            return;
        }

        DateTime now = DateTime.UtcNow;
        TryConfirmSigil(now);

        if (lastPlayerPosition is { } previous && Vector3.Distance(previous, me.Position) > TeleportJumpDistance)
        {
            OnTeleported(previous, me.Position, now);
        }

        lastPlayerPosition = me.Position;

        if (conditions[ConditionFlag.BetweenAreas]
            || conditions[ConditionFlag.BetweenAreas51]
            || conditions[ConditionFlag.OccupiedInCutSceneEvent]
            || conditions[ConditionFlag.WatchingCutscene]
            || conditions[ConditionFlag.OccupiedInEvent])
        {
            return;
        }

        if (conditions[ConditionFlag.InCombat])
        {
            if (activity != Activity.Idle)
            {
                logger.Info("[TowerNav] In combat — stopping {Activity}, BossMod moves from here", activity);
                StopMovement();
            }

            return;
        }

        if (now < nextDecisionAt)
        {
            return;
        }

        nextDecisionAt = now + DecisionInterval;

        if (!vnav.IsAvailable())
        {
            if (!warnedVnavMissing)
            {
                warnedVnavMissing = true;
                logger.Warn("[TowerNav] vnavmesh is not available — tower navigation does nothing");
            }

            return;
        }

        if (me.IsCasting)
        {
            return;
        }

        if (config.OpenCoffersInsideTower && TickCoffer(me, now))
        {
            return;
        }

        if (config.NavigateInsideTower)
        {
            TickFollow(me, now);
        }
        else if (activity != Activity.Idle)
        {
            StopMovement();
        }
    }

    public void Stop()
    {
        StopMovement();
        lastPlayerPosition = null;
        lastCrowdAnchor = null;
        sigilSearchCenter = null;
        confirmYesnoUntil = DateTime.MinValue;
    }

    private bool TickCoffer(IPlayerCharacter me, DateTime now)
    {
        IGameObject? coffer = FindCoffer(me, now);
        if (coffer == null)
        {
            if (activity == Activity.Coffer)
            {
                StopMovement();
            }

            return false;
        }

        if (activity != Activity.Coffer || activityObjectId != coffer.GameObjectId)
        {
            Begin(Activity.Coffer, coffer.GameObjectId, now);
            logger.Info(
                "[TowerNav] Coffer {Type} (BaseId {BaseId}) at {Position}, {Distance:F1}y away",
                new TreasureCoffer(coffer, data).GetCofferType(),
                coffer.BaseId,
                Format(coffer.Position),
                Vector3.Distance(me.Position, coffer.Position));
        }

        if (now - activityStartedAt > CofferGiveUp)
        {
            logger.Info("[TowerNav] Giving up on coffer {BaseId} after {Seconds}s", coffer.BaseId, CofferGiveUp.TotalSeconds);
            skipUntil[coffer.GameObjectId] = now + TimeSpan.FromMinutes(5);
            StopMovement();
            return false;
        }

        float distance = me.Position.Distance2D(coffer.Position);
        if (distance > InteractDistance)
        {
            MoveTo(me, coffer.Position, InteractDistance - 0.5f, now);
            if (distance > InteractDistance + 1.5f)
            {
                return true;
            }
        }
        else
        {
            StopVnav();
        }

        var native = (CsGameObject*)coffer.Address;
        if (native->GetIsTargetable() && EzThrottler.Throttle("ForkedTower.OpenCoffer", 1000))
        {
            logger.Info("[TowerNav] Opening coffer {BaseId} at {Distance:F1}y", coffer.BaseId, distance);
            TargetSystem.Instance()->InteractWithObject(native, false);
        }

        return true;
    }

    private IGameObject? FindCoffer(IPlayerCharacter me, DateTime now)
    {
        IGameObject? best = null;
        bool bestSilver = false;
        float bestDistance = float.MaxValue;

        foreach (IGameObject obj in objects)
        {
            if (obj.ObjectKind != ObjectKind.Treasure || !obj.IsValid() || obj.IsDead || IsSkipped(obj.GameObjectId, now))
            {
                continue;
            }

            float distance = me.Position.Distance2D(obj.Position);
            if (distance > CofferSearchRadius || MathF.Abs(obj.Position.Y - me.Position.Y) > CofferMaxHeightDifference)
            {
                continue;
            }

            // Untargetable = not revealed yet (hidden bronze coffers) or already gone.
            if (!((CsGameObject*)obj.Address)->GetIsTargetable() || OpenTreasureCofferChain.IsOpenedOrLooted(obj))
            {
                continue;
            }

            bool silver = new TreasureCoffer(obj, data).GetCofferType() == CofferType.Silver;
            if ((silver && !bestSilver) || (silver == bestSilver && distance < bestDistance))
            {
                best = obj;
                bestSilver = silver;
                bestDistance = distance;
            }
        }

        return best;
    }

    private void TickFollow(IPlayerCharacter me, DateTime now)
    {
        Vector3? anchor = FindCrowdAnchor(me, out int size, out string source);
        DetectCrowdLeftThroughSigil(me, anchor, now);

        if (sigilSearchCenter is { } center && now < sigilSearchUntil && TickSigil(me, center, now))
        {
            return;
        }

        if (now >= nextStatusLogAt)
        {
            nextStatusLogAt = now + StatusLogInterval;
            logger.Info(
                "[TowerNav] Status: {Activity}, me {Me}, {Source} anchor {Anchor} ({Size} players, {Distance}y)",
                activity,
                Format(me.Position),
                source,
                anchor is { } a ? Format(a) : "none",
                size,
                anchor is { } b ? Vector3.Distance(me.Position, b).ToString("F1") : "-");
        }

        if (anchor is not { } target)
        {
            if (activity == Activity.Follow)
            {
                logger.Info("[TowerNav] Lost the group — standing still");
                StopMovement();
            }

            return;
        }

        float distance = Vector3.Distance(me.Position, target);
        if (activity != Activity.Follow)
        {
            if (distance <= FollowStartDistance)
            {
                return;
            }

            Begin(Activity.Follow, 0, now);
            logger.Info("[TowerNav] Following {Source} ({Size} players) at {Anchor}, {Distance:F1}y away", source, size, Format(target), distance);
        }

        if (distance <= FollowStopDistance)
        {
            StopMovement();
            return;
        }

        MoveTo(me, target, FollowStopDistance - 1f, now);
    }

    /// <summary>The player nearest the middle of the biggest cluster — party members first, then everyone.</summary>
    private Vector3? FindCrowdAnchor(IPlayerCharacter me, out int size, out string source)
    {
        List<Vector3> candidates = PartyPositions(me);
        int minSize = 1;
        source = "party";
        if (candidates.Count == 0)
        {
            source = "crowd";
            minSize = MinCrowdSize;
            foreach (IGameObject obj in objects)
            {
                if (obj is IPlayerCharacter other && other.GameObjectId != me.GameObjectId && !other.IsDead)
                {
                    candidates.Add(other.Position);
                }
            }
        }

        size = 0;
        int bestIndex = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            int count = 0;
            for (int j = 0; j < candidates.Count; j++)
            {
                if (Vector3.Distance(candidates[i], candidates[j]) <= CrowdRadius)
                {
                    count++;
                }
            }

            float distance = Vector3.Distance(me.Position, candidates[i]);
            if (count > size || (count == size && distance < bestDistance))
            {
                size = count;
                bestIndex = i;
                bestDistance = distance;
            }
        }

        if (bestIndex < 0 || size < minSize)
        {
            return null;
        }

        Vector3 seed = candidates[bestIndex];
        List<Vector3> members = candidates.Where(c => Vector3.Distance(seed, c) <= CrowdRadius).ToList();
        Vector3 centroid = members.Aggregate(Vector3.Zero, (sum, c) => sum + c) / members.Count;
        return members.MinBy(c => Vector3.Distance(c, centroid));
    }

    private List<Vector3> PartyPositions(IPlayerCharacter me)
    {
        List<Vector3> positions = [];
        for (int i = 0; i < party.Length; i++)
        {
            IGameObject? member = party[i]?.GameObject;
            if (member == null || member.GameObjectId == me.GameObjectId || member.IsDead)
            {
                continue;
            }

            positions.Add(member.Position);
        }

        return positions;
    }

    // When the group we were standing with suddenly is somewhere else and a sigil sat next to it, they took it.
    private void DetectCrowdLeftThroughSigil(IPlayerCharacter me, Vector3? anchor, DateTime now)
    {
        if (lastCrowdAnchor is { } previous
            && now - lastCrowdAt < TimeSpan.FromSeconds(5)
            && Vector3.Distance(me.Position, previous) <= 40f
            && (anchor is not { } current || Vector3.Distance(current, previous) > CrowdJumpDistance))
        {
            IGameObject? sigil = FindSigilNear(previous, now);
            if (sigil != null && sigilSearchCenter == null)
            {
                logger.Info(
                    "[TowerNav] Group left {Previous} (now {Current}); sigil {BaseId} was next to it — taking it",
                    Format(previous),
                    anchor is { } a ? Format(a) : "none",
                    sigil.BaseId);
                sigilSearchCenter = previous;
                sigilSearchUntil = now + TimeSpan.FromSeconds(40);
            }
        }

        if (anchor is { } seen)
        {
            lastCrowdAnchor = seen;
            lastCrowdAt = now;
        }
    }

    private bool TickSigil(IPlayerCharacter me, Vector3 center, DateTime now)
    {
        IGameObject? sigil = FindSigilNear(center, now);
        if (sigil == null)
        {
            logger.Info("[TowerNav] No usable sigil near {Center} any more", Format(center));
            sigilSearchCenter = null;
            if (activity == Activity.Sigil)
            {
                StopMovement();
            }

            return false;
        }

        if (activity != Activity.Sigil || activityObjectId != sigil.GameObjectId)
        {
            Begin(Activity.Sigil, sigil.GameObjectId, now);
            logger.Info("[TowerNav] Walking to sigil {BaseId} at {Position}", sigil.BaseId, Format(sigil.Position));
        }

        if (now - activityStartedAt > SigilGiveUp)
        {
            logger.Info("[TowerNav] Sigil {BaseId} did not take us anywhere in {Seconds}s — giving up", sigil.BaseId, SigilGiveUp.TotalSeconds);
            skipUntil[sigil.GameObjectId] = now + TimeSpan.FromMinutes(2);
            sigilSearchCenter = null;
            StopMovement();
            return false;
        }

        float distance = me.Position.Distance2D(sigil.Position);
        if (distance > InteractDistance + 0.5f)
        {
            MoveTo(me, sigil.Position, InteractDistance - 0.5f, now);
            if (distance > InteractDistance + 2f)
            {
                return true;
            }
        }
        else
        {
            StopVnav();
        }

        if (EzThrottler.Throttle("ForkedTower.UseSigil", 1500))
        {
            logger.Info("[TowerNav] Using sigil {BaseId} at {Distance:F1}y", sigil.BaseId, distance);
            TargetSystem.Instance()->InteractWithObject((CsGameObject*)sigil.Address, false);
            confirmYesnoUntil = now + TimeSpan.FromSeconds(5);
        }

        return true;
    }

    private IGameObject? FindSigilNear(Vector3 center, DateTime now)
    {
        return objects
            .Where(o => TeleportSigilIds.Contains(o.BaseId)
                        && o.IsValid()
                        && !IsSkipped(o.GameObjectId, now)
                        && Vector3.Distance(center, o.Position) <= SigilNearCrowdRadius
                        && ((CsGameObject*)o.Address)->GetIsTargetable())
            .MinBy(o => Vector3.Distance(center, o.Position));
    }

    private void TryConfirmSigil(DateTime now)
    {
        if (now >= confirmYesnoUntil || !AddonHelpers.TryGetSelectYesno(out AddonSelectYesno* yesno))
        {
            return;
        }

        confirmYesnoUntil = DateTime.MinValue;
        if (ReturnYesNo.IsReturnConfirmation(&yesno->AtkUnitBase))
        {
            logger.Warn("[TowerNav] Sigil asked a Return question — leaving it alone");
            return;
        }

        var master = new AddonMaster.SelectYesno((nint)yesno);
        logger.Info("[TowerNav] Confirming sigil prompt: {Text}", master.Text);
        master.Yes();
    }

    private void OnTeleported(Vector3 from, Vector3 to, DateTime now)
    {
        logger.Info("[TowerNav] Moved {From} → {To} in one tick (teleport)", Format(from), Format(to));
        if (activity == Activity.Sigil)
        {
            skipUntil[activityObjectId] = now + TimeSpan.FromMinutes(2);
        }

        sigilSearchCenter = null;
        lastCrowdAnchor = null;
        StopMovement();
    }

    private void MoveTo(IPlayerCharacter me, Vector3 destination, float range, DateTime now)
    {
        bool busy = vnav.IsRunning() || vnav.IsPathfinding();
        bool moved = lastDestination is not { } last || Vector3.Distance(last, destination) > 3f;

        if (busy && (!moved || now - lastDestinationAt < RepathInterval))
        {
            return;
        }

        if (!busy && !moved)
        {
            if (now - lastDestinationAt < TimeSpan.FromSeconds(1.5))
            {
                return;
            }

            // vnav finished or gave up without getting us there.
            shortStops++;
            float remaining = Vector3.Distance(me.Position, destination);
            logger.Info("[TowerNav] vnav stopped {Remaining:F1}y short of {Destination} (#{Count})", remaining, Format(destination), shortStops);
            if (shortStops >= 2 && remaining <= 30f)
            {
                logger.Info("[TowerNav] Walking straight at {Destination}", Format(destination));
                lastDestinationAt = now;
                vnav.FollowPath([me.Position, destination], false);
                return;
            }
        }

        if (moved)
        {
            shortStops = 0;
        }

        lastDestination = destination;
        lastDestinationAt = now;
        vnav.PathfindAndMoveCloseTo(destination, false, range);
    }

    private void Begin(Activity next, ulong objectId, DateTime now)
    {
        if (activity != next)
        {
            StopVnav();
        }

        activity = next;
        activityObjectId = objectId;
        activityStartedAt = now;
        lastDestination = null;
        shortStops = 0;
    }

    private void StopMovement()
    {
        if (activity != Activity.Idle || lastDestination != null)
        {
            StopVnav();
        }

        activity = Activity.Idle;
        activityObjectId = 0;
        lastDestination = null;
        shortStops = 0;
    }

    private void StopVnav()
    {
        if (vnav.IsRunning() || vnav.IsPathfinding())
        {
            vnav.Stop();
        }
    }

    private bool IsSkipped(ulong objectId, DateTime now) => skipUntil.TryGetValue(objectId, out DateTime until) && now < until;

    private static string Format(Vector3 v) => $"({v.X:F1}, {v.Y:F1}, {v.Z:F1})";
}
