using BOCCHI.Common.Config;
using BOCCHI.Common.Data.Aethernet;
using BOCCHI.Common.Data.Zones;
using BOCCHI.MobFarmer.Data;
using BOCCHI.MobFarmer.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using Ocelot.Extensions;
using Ocelot.Services.Logger;
using Ocelot.Services.Pathfinding;
using Ocelot.Services.PlayerState;
using Ocelot.States.Flow;
using System.Numerics;

namespace BOCCHI.MobFarmer.StateMachine.Handlers;

public class WaitingHandler
(
    MobFarmerConfig config,
    MovementConfig movementConfig,
    IMobFarmer farmer,
    IMobScanner scanner,
    ICondition conditions,
    IObjectTable objects,
    IPathfinder pathfinder,
    IZoneProvider zones,
    IPlayer player,
    FarmerTravel travel,
    ILogger<WaitingHandler> logger
) : FlowStateHandler<FarmerPhase>(FarmerPhase.Waiting)
{
    private const float ArriveRange = 8f;

    private const float PathArriveRange = 2f;

    // Beyond this, travel via ActivityNavigation (aethernet hop + auto-mount) instead of a raw vnav walk.
    private const float LongTravelDistance = 60f;

    private static readonly TimeSpan MountBeforeTravelTimeout = TimeSpan.FromSeconds(4);

    private const ulong HomeWatchKey = 0;

    private readonly FarmerWalkStuckAssist stuckAssist = new();

    // Navigation already ended short of the spot once — finish on the raw walk with stuck recovery.
    private bool longTravelExhausted;

    private DateTime? mountBeforeTravelDeadline;

    public override void Exit(FarmerPhase next)
    {
        stuckAssist.Reset();
        ResetTravel();
        base.Exit(next);
    }

    public override FarmerPhase? Handle()
    {
        if (scanner.InCombat.Any())
        {
            return FarmerPhase.Fighting;
        }

        if (config.OnlyStartOutOfCombat && conditions[ConditionFlag.InCombat])
        {
            return null;
        }

        if (conditions[ConditionFlag.InCombat])
        {
            return FarmerPhase.Fighting;
        }

        float homeDistance = player.Position.Distance2D(farmer.StartingPoint);
        if (farmer.NeedsApproachSpot)
        {
            if (homeDistance <= ArriveRange)
            {
                farmer.MarkArrivedAtSpot();
                stuckAssist.Reset();
                ResetTravel();
            }
            else
            {
                if (TryLongTravel(homeDistance))
                {
                    return null;
                }

                if (TryRecoverFromStuck(homeDistance, farmer.StartingPoint))
                {
                    return null;
                }

                if (pathfinder.GetState() == PathfindingState.Idle)
                {
                    IssuePath(farmer.StartingPoint);
                }

                MountWait.TryCastIfNeeded(
                    conditions,
                    objects,
                    farmer.StartingPoint,
                    movementConfig.ShouldAutoMount,
                    movementConfig.PreferredMountId,
                    zones.GetZone().IsInBasecamp(),
                    zones.GetZone());

                return null;
            }
        }

        int free = MobFarmerPack.CountTowardMinimum(scanner.NotInCombat, config.CountSpecialMobsTowardMinimum);
        if (free == 0)
        {
            return null;
        }

        return free >= config.MinimumMobsToStartLoop ? FarmerPhase.Buffing : null;
    }

    private bool TryLongTravel(float distance)
    {
        if (travel.Destination is { } destination && destination != farmer.StartingPoint)
        {
            // Spot changed (claimed by someone else) — route to the new one.
            ResetTravel();
        }

        if (longTravelExhausted || distance <= LongTravelDistance)
        {
            return false;
        }

        if (travel.IsActive)
        {
            return true;
        }

        if (travel.Destination != null)
        {
            logger.Debug("Mob Farmer: navigation ended {Distance:F0}y short of the spot — walking the rest", distance);
            travel.Forget();
            longTravelExhausted = true;
            return false;
        }

        if (!travel.CanStart)
        {
            longTravelExhausted = true;
            return false;
        }

        if (WaitForMountBeforeTravel())
        {
            return true;
        }

        mountBeforeTravelDeadline = null;
        pathfinder.Stop();
        stuckAssist.Reset();
        string name = farmer.CurrentSpotName ?? "Mob Farmer spot";
        logger.Info("Mob Farmer: traveling to {Spot} ({Distance:F0}y away)", name, distance);
        travel.Start(farmer.StartingPoint, name);
        return true;
    }

    /// <summary>
    ///     Mount while standing still before a long walk — casting Mount while vnav is moving us can
    ///     get interrupted. Near an aetheryte we skip it: the route will probably hop first.
    /// </summary>
    private bool WaitForMountBeforeTravel()
    {
        if (!movementConfig.ShouldAutoMount
            || conditions[ConditionFlag.Mounted]
            || conditions[ConditionFlag.InCombat]
            || zones.GetZone().IsWithinLifestreamRange(player.Position))
        {
            return false;
        }

        mountBeforeTravelDeadline ??= DateTime.UtcNow + MountBeforeTravelTimeout;
        if (DateTime.UtcNow >= mountBeforeTravelDeadline)
        {
            return false;
        }

        if (pathfinder.GetState() != PathfindingState.Idle)
        {
            pathfinder.Stop();
        }

        if (!conditions[ConditionFlag.Mounting]
            && !conditions[ConditionFlag.Mounting71]
            && !conditions[ConditionFlag.Casting]
            && EzThrottler.Throttle("MobFarmer::MountBeforeTravel", 750))
        {
            MountWait.TryCast(movementConfig.PreferredMountId);
        }

        return true;
    }

    private void ResetTravel()
    {
        travel.Cancel();
        longTravelExhausted = false;
        mountBeforeTravelDeadline = null;
    }

    private bool TryRecoverFromStuck(float distance, Vector3 goal)
    {
        FarmerWalkStuckAssist.Recovery recovery = stuckAssist.Tick(
            HomeWatchKey,
            distance,
            goal,
            pathfinder.GetState());

        switch (recovery)
        {
            case FarmerWalkStuckAssist.Recovery.Nudge:
                logger.Debug("Mob Farmer: stuck approaching farm spot — nudging sideways");
                pathfinder.Stop();
                IssuePath(FarmerWalkStuckAssist.LateralNudge(player.Position, goal));
                return true;

            case FarmerWalkStuckAssist.Recovery.RepathGoal:
                logger.Debug("Mob Farmer: still stuck on farm spot — repathing");
                pathfinder.Stop();
                IssuePath(goal);
                return true;

            case FarmerWalkStuckAssist.Recovery.GiveUp:
                logger.Info(
                    "Mob Farmer: could not reach farm spot after stuck recoveries — continuing from here");
                pathfinder.Stop();
                farmer.MarkArrivedAtSpot();
                stuckAssist.Reset();
                return true;

            default:
                return false;
        }
    }

    private void IssuePath(Vector3 destination)
    {
        pathfinder.PathfindAndMoveTo(new(destination)
        {
            AllowFlying = false,
            DistanceThreshold = PathArriveRange,
        });
    }
}
