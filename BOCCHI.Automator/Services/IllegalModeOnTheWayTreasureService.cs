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
///     that sit inside a corridor around the line player → leg destination, then resume travel.
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

    /// <summary>Height difference from the line (interpolated) — skips coffers on a ledge above / below.</summary>
    private const float MaxHeightDelta = 20f;

    /// <summary>Legs shorter than this are about to arrive — not worth a detour.</summary>
    private const float MinLegLength = 15f;

    private static readonly TimeSpan DetourTimeout = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan FailedCofferSkip = TimeSpan.FromMinutes(3);

    private readonly Dictionary<ulong, DateTime> skipUntilUtc = [];

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

        if (!CanStartDetour(out Vector3 legDestination))
        {
            return;
        }

        if (TryFindCofferOnTheWay(legDestination, out IGameObject? coffer))
        {
            BeginDetour(coffer!, legDestination);
        }
    }

    private bool IsEnabled() =>
        (context.IsIllegalMode || context.IsCompletionist)
        && !context.IsPotsAndTreasure
        && automatorConfig.UsesOnTheWayTreasureHunt
        && zones.GetZone().IsOccultCrescentZone();

    private bool CanStartDetour(out Vector3 legDestination)
    {
        legDestination = default;

        if (automator.SuspendedForTreasure
            || automator.SuspendedForShopping
            || automator.FightingInForkedTower
            || automator.CurrentState != AutomatorState.Pathfinding
            || hunter.Running
            || conditions[ConditionFlag.InCombat]
            || conditions[ConditionFlag.BetweenAreas]
            || conditions[ConditionFlag.Unconscious]
            || memory.TryRemember<NavigationInterruptedMemory>(out NavigationInterruptedMemory _))
        {
            return false;
        }

        // Forked Tower registration is time critical — no detours.
        if (!memory.TryRemember<GoalMemory>(out GoalMemory goal) || goal.Goal.GoalType is ForkedTowerGoal)
        {
            return false;
        }

        // Only walking legs: teleport / return steps have no line to follow.
        if (!memory.TryRemember<GoalPathStepMemory>(out GoalPathStepMemory path)
            || path.GetNextPathStep()?.PathStepData is not Pathfind(var destination, _))
        {
            return false;
        }

        legDestination = destination;
        return true;
    }

    private bool TryFindCofferOnTheWay(Vector3 legDestination, out IGameObject? best)
    {
        best = null;

        Vector3 origin = player.Position;
        Vector2 start = new(origin.X, origin.Z);
        Vector2 end = new(legDestination.X, legDestination.Z);
        float legLength = Vector2.Distance(start, end);
        if (legLength < MinLegLength)
        {
            return false;
        }

        Vector2 direction = (end - start) / legLength;
        float bestDetour = float.MaxValue;
        DateTime now = DateTime.UtcNow;

        foreach (IGameObject obj in objects)
        {
            if (obj is not { ObjectKind: ObjectKind.Treasure, IsDead: false } || !obj.IsValid())
            {
                continue;
            }

            if (skipUntilUtc.TryGetValue(obj.GameObjectId, out DateTime until))
            {
                if (now < until)
                {
                    continue;
                }

                skipUntilUtc.Remove(obj.GameObjectId);
            }

            Vector3 position = obj.Position;
            if (TreasurePathing.IsUnloadAltitude(position) || OpenTreasureCofferChain.IsOpenedOrLooted(obj))
            {
                continue;
            }

            CofferType type = new TreasureCoffer(obj, data).GetCofferType();
            if (type is not (CofferType.Bronze or CofferType.Silver)
                || (treasureConfig.HuntSilverChestsOnly && type != CofferType.Silver))
            {
                continue;
            }

            Vector2 flat = new(position.X, position.Z);
            float t = Math.Clamp(Vector2.Dot(flat - start, direction) / legLength, 0f, 1f);
            if (Vector2.Distance(flat, start + (end - start) * t) > CorridorHalfWidth)
            {
                continue;
            }

            float lineY = origin.Y + (legDestination.Y - origin.Y) * t;
            if (MathF.Abs(position.Y - lineY) > MaxHeightDelta)
            {
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

        return best != null;
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
