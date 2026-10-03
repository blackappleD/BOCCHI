using BOCCHI.Automator.Data;
using BOCCHI.Common.Config;
using BOCCHI.Common.Data.Goals;
using BOCCHI.Common.Data.Paths;
using BOCCHI.Common.Data.StateMemory;
using BOCCHI.Common.Data.Zones;
using BOCCHI.Common.Services;
using BOCCHI.Treasure.ChainRecipes;
using BOCCHI.Treasure.Data;
using BOCCHI.Treasure.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Ocelot.Chain;
using Ocelot.Chain.Extensions;
using Ocelot.Ipc.VNavmesh;
using Ocelot.Lifecycle;
using Ocelot.Services.Logger;
using Ocelot.Services.PlayerState;
using System.Numerics;

namespace BOCCHI.Automator.Services;

/// <summary>
///     "On the way" automatic treasure hunt: while Illegal Mode walks a travel leg, open coffers
///     that sit inside a corridor around the line player → leg destination (or right next to the
///     player in any direction), then resume travel.
///     Replaces the post-activity survey + full hunt route when
///     <see cref="AutomatorConfig.AutoTreasureHuntOnTheWay" /> is on.
/// </summary>
public class IllegalModeOnTheWayTreasureService
(
    IAutomator automator,
    IAutomatorContext context,
    IAutomatorMemory memory,
    ITreasureHunter hunter,
    IObjectTable objects,
    IDataManager data,
    IPlayer player,
    ICondition conditions,
    IZoneProvider zones,
    IChainFactory chains,
    IChainManager chainManager,
    IVNavmeshIpc vnav,
    AutomatorConfig automatorConfig,
    TreasureConfig treasureConfig,
    ILogger<IllegalModeOnTheWayTreasureService> logger
) : IOnUpdate
{
    private const string ChainName = "IllegalMode::OnTheWayCoffer";

    /// <summary>
    ///     Max 2D distance from the travel segment player → leg destination for a coffer to count as on
    ///     the way. Measured to the segment, so coffers beside or behind the player, or a little past the
    ///     destination, count too as long as they are this close.
    /// </summary>
    private const float CorridorHalfWidth = 25f;

    /// <summary>Coffers this close (2D) to the player count in any direction, wherever the line runs.</summary>
    private const float NearRadius = 45f;

    /// <summary>Height difference from the line (interpolated) — skips coffers on a ledge above / below.</summary>
    private const float MaxHeightDelta = 20f;

    /// <summary>Legs shorter than this are about to arrive — not worth a detour.</summary>
    private const float MinLegLength = 15f;

    /// <summary>Unopened coffers this close that we leave alone get a (throttled) log line saying why.</summary>
    private const float DiagnosticRange = 60f;

    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan DetourTimeout = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan FailedCofferSkip = TimeSpan.FromMinutes(3);

    private readonly Dictionary<ulong, DateTime> skipUntilUtc = [];

    private readonly Dictionary<ulong, DateTime> lastSkipReportUtc = [];

    private Detour? detour;

    public int Order => 1;

    public UpdateLimit UpdateLimit =>
        new()
        {
            Mode = UpdateLimitMode.Milliseconds,
            Limit = 250
        };

    public void Update()
    {
        if (!IsEnabled())
        {
            if (detour != null)
            {
                EndDetour("disabled", resumeTravel: true);
            }

            return;
        }

        if (detour != null)
        {
            UpdateDetour(detour);
            return;
        }

        if (!CanStartDetour(out Vector3 legDestination, out string blocked))
        {
            ReportSkippedCoffer(FindNearestCandidate(), blocked);
            return;
        }

        if (TryFindCofferOnTheWay(legDestination, out IGameObject? coffer, out IGameObject? rejected, out string? reason))
        {
            BeginDetour(coffer!, legDestination);
            return;
        }

        ReportSkippedCoffer(rejected, reason);
    }

    private bool IsEnabled() =>
        (context.IsIllegalMode || context.IsCompletionist)
        && !context.IsPotsAndTreasure
        && automatorConfig.UsesOnTheWayTreasureHunt
        && zones.GetZone().IsOccultCrescentZone();

    private bool CanStartDetour(out Vector3 legDestination, out string blocked)
    {
        legDestination = default;

        blocked = automator.SuspendedForTreasure ? "suspended for treasure"
            : automator.SuspendedForShopping ? "suspended for shopping"
            : automator.FightingInForkedTower ? "fighting in the Forked Tower"
            : automator.CurrentState != AutomatorState.Pathfinding ? $"automator state {automator.CurrentState?.ToString() ?? "none"}"
            : hunter.Running ? "treasure hunter running"
            : conditions[ConditionFlag.InCombat] ? "in combat"
            : conditions[ConditionFlag.BetweenAreas] ? "between areas"
            : conditions[ConditionFlag.Unconscious] ? "unconscious"
            : memory.TryRemember<NavigationInterruptedMemory>(out NavigationInterruptedMemory _) ? "navigation interrupted"
            : string.Empty;

        if (blocked.Length > 0)
        {
            return false;
        }

        // Forked Tower registration is time critical — no detours.
        if (!memory.TryRemember<GoalMemory>(out GoalMemory goal))
        {
            blocked = "no goal";
            return false;
        }

        if (goal.Goal.GoalType is ForkedTowerGoal)
        {
            blocked = "Forked Tower goal";
            return false;
        }

        // Only walking legs: teleport / return steps have no line to follow.
        if (!memory.TryRemember<GoalPathStepMemory>(out GoalPathStepMemory path))
        {
            blocked = "no path";
            return false;
        }

        object? step = path.GetNextPathStep()?.PathStepData;
        if (step is not Pathfind(var destination, _))
        {
            blocked = $"next step is {step?.GetType().Name ?? "none"}";
            return false;
        }

        legDestination = destination;
        return true;
    }

    /// <summary>Unopened bronze / silver coffer the on-the-way hunt would consider at all.</summary>
    private bool IsCandidate(IGameObject obj)
    {
        if (obj is not { ObjectKind: ObjectKind.Treasure, IsDead: false } || !obj.IsValid())
        {
            return false;
        }

        if (TreasurePathing.IsUnloadAltitude(obj.Position) || OpenTreasureCofferChain.IsOpenedOrLooted(obj))
        {
            return false;
        }

        CofferType type = new TreasureCoffer(obj, data).GetCofferType();
        return type is CofferType.Bronze or CofferType.Silver
               && (!treasureConfig.HuntSilverChestsOnly || type == CofferType.Silver);
    }

    private IGameObject? FindNearestCandidate()
    {
        Vector3 origin = player.Position;
        IGameObject? nearest = null;
        float nearestDistance = DiagnosticRange;
        foreach (IGameObject obj in objects)
        {
            if (obj.ObjectKind != ObjectKind.Treasure)
            {
                continue;
            }

            float distance = Vector3.Distance(origin, obj.Position);
            if (distance < nearestDistance && IsCandidate(obj))
            {
                nearest = obj;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    /// <summary>
    ///     Logs why a nearby unopened coffer was left alone — once per coffer every
    ///     <see cref="DiagnosticInterval" />, so "it walked right past it" has an answer in the log.
    /// </summary>
    private void ReportSkippedCoffer(IGameObject? coffer, string? reason)
    {
        if (coffer == null || reason == null)
        {
            return;
        }

        float distance = Vector3.Distance(player.Position, coffer.Position);
        if (distance > DiagnosticRange)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (lastSkipReportUtc.TryGetValue(coffer.GameObjectId, out DateTime last) && now - last < DiagnosticInterval)
        {
            return;
        }

        if (lastSkipReportUtc.Count > 64)
        {
            lastSkipReportUtc.Clear();
        }

        lastSkipReportUtc[coffer.GameObjectId] = now;
        logger.Info("Illegal Mode: not opening coffer {Distance:F0}y away on the way ({Reason})", distance, reason);
    }

    private bool TryFindCofferOnTheWay(
        Vector3 legDestination,
        out IGameObject? best,
        out IGameObject? rejected,
        out string? reason)
    {
        best = null;
        rejected = null;
        reason = null;

        Vector3 origin = player.Position;
        Vector2 start = new(origin.X, origin.Z);
        Vector2 end = new(legDestination.X, legDestination.Z);
        float legLength = Vector2.Distance(start, end);

        // A leg about to arrive has no line worth following — only coffers right next to the player count.
        bool shortLeg = legLength < MinLegLength;
        Vector2 direction = shortLeg ? Vector2.Zero : (end - start) / legLength;
        float bestDetour = float.MaxValue;
        float nearestRejected = DiagnosticRange;
        IGameObject? nearestRejectedCoffer = null;
        string? rejectReason = null;
        DateTime now = DateTime.UtcNow;

        foreach (IGameObject obj in objects)
        {
            if (obj.ObjectKind != ObjectKind.Treasure || !IsCandidate(obj))
            {
                continue;
            }

            Vector3 position = obj.Position;
            float fromPlayer = Vector3.Distance(origin, position);

            if (skipUntilUtc.TryGetValue(obj.GameObjectId, out DateTime until))
            {
                if (now < until)
                {
                    Reject(obj, fromPlayer, "failed to open it recently");
                    continue;
                }

                skipUntilUtc.Remove(obj.GameObjectId);
            }

            Vector2 flat = new(position.X, position.Z);
            bool near = Vector2.Distance(start, flat) <= NearRadius;
            if (!near && shortLeg)
            {
                Reject(obj, fromPlayer, $"leg nearly done ({legLength:F0}y left)");
                continue;
            }

            float t = shortLeg ? 0f : Math.Clamp(Vector2.Dot(flat - start, direction) / legLength, 0f, 1f);
            float fromLine = Vector2.Distance(flat, start + (end - start) * t);
            if (!near && fromLine > CorridorHalfWidth)
            {
                Reject(obj, fromPlayer, $"{fromLine:F0}y off the travel line");
                continue;
            }

            // Near coffers are compared with the player's own height, line ones with the line at that point.
            float referenceY = near ? origin.Y : origin.Y + (legDestination.Y - origin.Y) * t;
            float heightDelta = position.Y - referenceY;
            if (MathF.Abs(heightDelta) > MaxHeightDelta)
            {
                Reject(obj, fromPlayer, $"{heightDelta:+0;-0}y height difference");
                continue;
            }

            // Extra walking the coffer adds to the leg — prefers coffers along the way over ones behind.
            float detourCost = Vector2.Distance(start, flat) + Vector2.Distance(flat, end) - legLength;
            if (detourCost < bestDetour)
            {
                bestDetour = detourCost;
                best = obj;
            }
        }

        rejected = nearestRejectedCoffer;
        reason = rejectReason;
        return best != null;

        void Reject(IGameObject obj, float distance, string why)
        {
            if (distance < nearestRejected)
            {
                nearestRejected = distance;
                nearestRejectedCoffer = obj;
                rejectReason = why;
            }
        }
    }

    private void BeginDetour(IGameObject coffer, Vector3 legDestination)
    {
        automator.SetSuspendedForTreasure(true);

        Task<ChainResult> chain = chainManager.Manage(
            chains.Create(ChainName)
                .Then<OpenTreasureCofferChain, TreasureOpenTarget>(new TreasureOpenTarget(coffer.Position)));

        detour = new Detour(coffer.GameObjectId, DateTime.UtcNow, chain);
        logger.Info(
            "Illegal Mode: opening coffer on the way ({Distance:F0}y away, leg {Leg:F0}y)",
            Vector3.Distance(player.Position, coffer.Position),
            Vector3.Distance(player.Position, legDestination));
    }

    private void UpdateDetour(Detour current)
    {
        // Something else (pot chest farm, mode switch) took Illegal Mode back — just let go.
        if (!automator.SuspendedForTreasure)
        {
            EndDetour("travel resumed elsewhere", resumeTravel: false);
            return;
        }

        if (conditions[ConditionFlag.InCombat])
        {
            EndDetour("combat", resumeTravel: true);
            return;
        }

        IGameObject? coffer = objects.FirstOrDefault(o => o.GameObjectId == current.ObjectId);
        if (coffer == null || !coffer.IsValid() || coffer.IsDead || OpenTreasureCofferChain.IsOpenedOrLooted(coffer))
        {
            EndDetour("coffer opened", resumeTravel: true);
            return;
        }

        if (current.Chain.IsCompleted)
        {
            Skip(current.ObjectId);
            EndDetour("open failed", resumeTravel: true);
            return;
        }

        if (DateTime.UtcNow - current.StartedUtc >= DetourTimeout)
        {
            Skip(current.ObjectId);
            EndDetour("timed out", resumeTravel: true);
        }
    }

    private void Skip(ulong objectId) => skipUntilUtc[objectId] = DateTime.UtcNow + FailedCofferSkip;

    private void EndDetour(string reason, bool resumeTravel)
    {
        detour = null;
        chainManager.CancelWhere(name => name == ChainName);
        if (vnav.IsRunning() || vnav.IsPathfinding())
        {
            vnav.Stop();
        }

        if (resumeTravel)
        {
            automator.SetSuspendedForTreasure(false);
        }

        logger.Info("Illegal Mode: on-the-way coffer detour ended ({Reason})", reason);
    }

    private sealed record Detour(ulong ObjectId, DateTime StartedUtc, Task<ChainResult> Chain);
}
