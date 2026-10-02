using BOCCHI.Common.Config;
using BOCCHI.Common.Data.MobFarmer;
using BOCCHI.Common.Data.Mobs;
using BOCCHI.Common.Data.Zones;
using BOCCHI.Common.Extensions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Ocelot.Extensions;
using Ocelot.Ipc.VNavmesh;
using Ocelot.Services.PlayerState;
using System.Numerics;

namespace BOCCHI.MobFarmer.Services;

public sealed class FarmerSpotSession(
    MobFarmerConfig config,
    IPlayer player,
    IObjectTable objects,
    IZoneProvider zones,
    IDataManager data,
    IVNavmeshIpc vnav)
{
    // Seed above any floor so the navmesh snap lands on the topmost surface at that X/Z.
    private const float AreaSnapSeedHeight = 1500f;

    private const float AreaSnapExtentXZ = 25f;

    private readonly HashSet<int> claimedIndices = [];

    private readonly HashSet<Mob> claimedAreas = [];

    /// <summary>Set when no configured spot applies and we're heading to a selected mob's known area.</summary>
    private Mob? currentArea;

    private DateTimeOffset? contestedSinceUtc;

    public FarmSpot? Current { get; private set; }

    public Vector3 Origin { get; private set; }

    public Vector3? StackPoint => Current?.StackPoint;

    public string? Name => Current?.Name;

    public bool NeedsApproach { get; private set; }

    public int EffectiveMinimumMobsToStartFight =>
        Current is { MinimumMobsToStartFight: > 0 } spot
            ? spot.MinimumMobsToStartFight
            : config.MinimumMobsToStartFight;

    public void Begin()
    {
        claimedIndices.Clear();
        claimedAreas.Clear();
        contestedSinceUtc = null;
        currentArea = null;
        Current = SelectBest(player.Position) ?? SelectMobArea(player.Position);
        Origin = Current?.Origin ?? player.Position;
        NeedsApproach = Current != null && player.Position.Distance2D(Origin) > 8f;
    }

    public void Reset()
    {
        claimedIndices.Clear();
        claimedAreas.Clear();
        contestedSinceUtc = null;
        currentArea = null;
        Current = null;
        Origin = Vector3.Zero;
        NeedsApproach = false;
    }

    public void MarkArrived() => NeedsApproach = false;

    public void RequireApproachIfAway()
    {
        if (Origin != Vector3.Zero && player.Position.Distance2D(Origin) > 8f)
        {
            NeedsApproach = true;
        }
    }

    public bool TickClaimed(IMobScanner scanner)
    {
        if (Current == null || (config.Spots.Count == 0 && currentArea == null))
        {
            contestedSinceUtc = null;
            return false;
        }

        if (!IsClaimedNow(scanner))
        {
            contestedSinceUtc = null;
            return false;
        }

        contestedSinceUtc ??= DateTimeOffset.UtcNow;
        if (DateTimeOffset.UtcNow - contestedSinceUtc < TimeSpan.FromSeconds(config.ClaimedSpotSeconds))
        {
            return false;
        }

        if (currentArea is { } area)
        {
            claimedAreas.Add(area);
        }
        else
        {
            int index = config.Spots.IndexOf(Current);
            if (index >= 0)
            {
                claimedIndices.Add(index);
            }
        }

        contestedSinceUtc = null;
        Mob? previousArea = currentArea;
        currentArea = null;
        FarmSpot? next = SelectBest(player.Position) ?? SelectMobArea(player.Position);
        if (next == null || ReferenceEquals(next, Current))
        {
            currentArea = previousArea;
            return false;
        }

        Current = next;
        Origin = next.Origin;
        NeedsApproach = true;
        return true;
    }

    private bool IsClaimedNow(IMobScanner scanner)
    {
        bool contested = scanner.Contested.Any();
        bool noFree = !scanner.NotInCombat.Any();
        if (contested && noFree)
        {
            return true;
        }

        Vector3 watch = StackPoint ?? Origin;
        float radius = config.ClaimedPlayerRadius;
        ulong localId = objects.LocalPlayer?.GameObjectId ?? 0;
        return objects.OfType<IPlayerCharacter>()
            .Any(p => p.GameObjectId != localId
                      && p.Position.Distance2D(watch) <= radius);
    }

    private FarmSpot? SelectBest(Vector3 from)
    {
        List<(int Index, FarmSpot Spot)> enabled = [];
        for (int i = 0; i < config.Spots.Count; i++)
        {
            FarmSpot spot = config.Spots[i];
            if (spot.Enabled && !claimedIndices.Contains(i))
            {
                enabled.Add((i, spot));
            }
        }

        if (enabled.Count == 0)
        {
            return null;
        }

        return enabled
            .OrderByDescending(e => e.Spot.Priority)
            .ThenBy(e => from.Distance2D(e.Spot.Origin))
            .Select(e => e.Spot)
            .First();
    }

    /// <summary>
    ///     Fallback when no configured spot is usable: walk to the nearest known area of a selected mob
    ///     in this zone. Builds a throwaway spot (not saved to config).
    /// </summary>
    private FarmSpot? SelectMobArea(Vector3 from)
    {
        if (!config.AutoTravelToMobArea)
        {
            return null;
        }

        ZoneId zone = zones.GetZone().ZoneId;
        (Mob Mob, Vector3 Position)? best = null;
        foreach (Mob mob in config.Mobs.Distinct())
        {
            if (claimedAreas.Contains(mob)
                || !MobSpawnAreas.TryGetWorldXZ(mob, zone, data, out Vector2 xz))
            {
                continue;
            }

            Vector3 position = new(xz.X, from.Y, xz.Y);
            if (best == null || from.Distance2D(position) < from.Distance2D(best.Value.Position))
            {
                best = (mob, position);
            }
        }

        if (best is not { } pick)
        {
            return null;
        }

        Vector3 seed = new(pick.Position.X, AreaSnapSeedHeight, pick.Position.Z);
        // No floor found (vnav missing / off-mesh): keep our own height and let the path request snap it.
        Vector3 origin = vnav.TryFindPointOnFloor(seed, AreaSnapExtentXZ, out Vector3 floored)
                         && floored.Y < AreaSnapSeedHeight - 1f
            ? floored
            : pick.Position;

        currentArea = pick.Mob;
        FarmSpot spot = new() { Name = MobData.GetDisplayName(pick.Mob, data) };
        spot.SetOrigin(origin);
        return spot;
    }
}
