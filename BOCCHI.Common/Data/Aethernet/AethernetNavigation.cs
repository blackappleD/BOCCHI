using BOCCHI.Common.Data.Paths;
using BOCCHI.Common.Data.Zones;
using BOCCHI.Common.Data.Zones.Graph;
using BOCCHI.Common.Services;
using Ocelot.Extensions;
using System.Numerics;

namespace BOCCHI.Common.Data.Aethernet;

public static class AethernetNavigation
{
    /// <summary>Arrival radius while closing on aetheryte rings.</summary>
    public const float PathfindArrivalRadius = 0.5f;

    /// <summary>
    ///     Extra slack when deciding we are already at the stand-off (vnav often stops a touch short).
    ///     Treasure hunt uses the same figure.
    /// </summary>
    public const float PathfindArrivalSlack = 0.35f;

    public const float EdgeClearance = AethernetData.LifestreamEdgeClearance;

    public static Vector3 GetInteractPosition(this AethernetData data) => data.Destination != Vector3.Zero ? data.Destination : data.Position;

    public static Vector3 GetInteractPosition(this Node node)
    {
        if (node.Metadata is TeleportNodeMetadata { Destination: var destination } && destination != Vector3.Zero)
        {
            return destination;
        }

        return node.Position;
    }

    /// <summary>Magenta ring — solid body / Lifestream zone.</summary>
    public static float GetBodyRadius(this AethernetData data) => MathF.Max(2f, data.DeadRadius);

    /// <summary>Cyan ring — outer edge of the idle band.</summary>
    public static float GetIdleOuterRadius(this AethernetData data) => data.GetBodyRadius() + EdgeClearance;

    /// <summary>Midpoint of the idle band (between magenta and cyan).</summary>
    public static float GetIdleWaitRadius(this AethernetData data) =>
        data.GetBodyRadius() + (EdgeClearance * 0.5f);

    /// <summary>
    ///     Magenta ring + pathfind stop slack. Body alone left Base Camp stuck ~0.2y outside
    ///     the ring after vnav arrived (crystal Y vs Destination footpad Y).
    /// </summary>
    public static float GetLifestreamReadyRadius(this AethernetData data) =>
        data.GetBodyRadius() + PathfindArrivalRadius;

    /// <summary>
    ///     Walk target on the magenta body ring (not inside the mesh). Pathfind may stop a
    ///     little short; ready radius includes <see cref="PathfindArrivalRadius"/> slack.
    /// </summary>
    public static float GetLifestreamApproachRadius(this AethernetData data) => data.GetBodyRadius();

    /// <summary>
    ///     Approach-side point on the magenta Lifestream ring.
    ///     Idle wandering uses <see cref="GetIdleWaitPosition"/> / cyan candidates instead.
    /// </summary>
    public static Vector3 GetCampStandOffPosition(this AethernetData data, Vector3? from = null)
        => GetRingPosition(data.Position, data.GetInteractPosition(), data.GetLifestreamApproachRadius(), from);

    public static Vector3 GetCampStandOffPosition(this Node node, Vector3? from = null)
        => GetRingPosition(node.Position, node.GetInteractPosition(), GetNodeBodyRadius(node), from);

    /// <summary>Magenta + arrival slack — same meaning as <see cref="GetLifestreamReadyRadius"/>.</summary>
    public static float GetNodeLifestreamReadyRadius(this Node node) =>
        GetNodeBodyRadius(node) + PathfindArrivalRadius;

    private static float DefaultBodyRadius => MathF.Max(2f, AethernetData.DefaultDeadRadius);

    private static float GetNodeBodyRadius(Node node) =>
        node.Metadata is TeleportNodeMetadata { DeadRadius: var dead }
            ? MathF.Max(2f, dead)
            : DefaultBodyRadius;

    public static Vector3 GetIdleWaitPosition(this AethernetData data, Vector3? from = null)
        => GetRingPosition(data.Position, data.GetInteractPosition(), data.GetIdleWaitRadius(), from);

    private static Vector3 GetRingPosition(Vector3 crystal, Vector3 interactOrHint, float radius, Vector3? from = null)
    {
        // Prefer the approach side (player). Destination is only a fallback facing.
        Vector3 dir = FlatOffset(from ?? interactOrHint, crystal);
        if (dir.LengthSquared() < 0.25f)
        {
            dir = FlatOffset(interactOrHint, crystal);
        }

        if (dir.LengthSquared() < 0.25f)
        {
            dir = new Vector3(1f, 0f, 0f);
        }

        // Always the outer ring on that side. Shrinking to Destination distance put the
        // target inside the crystal; floor-snap then dragged it to the far-side pad.
        // Use Destination / interact Y when set — crystal.Y is often above the walkable pad
        // (North Horn Base Camp: crystal ~259.7, Destination footpad ~258.5).
        Vector3 onRing = crystal + Vector3.Normalize(dir) * MathF.Max(radius, 0.5f);
        float standY = interactOrHint != Vector3.Zero ? interactOrHint.Y : crystal.Y;
        return new Vector3(onRing.X, standY, onRing.Z);
    }

    private static Vector3 FlatOffset(Vector3 point, Vector3 origin)
    {
        Vector3 d = point - origin;
        d.Y = 0f;
        return d;
    }

    public static IEnumerable<AethernetData> EnumerateAetherytes(this IZone zone) => zone.GetAetherytes();

    /// <summary>
    ///     Shards Lifestream can actually land on. Base camp is always included; other pads follow
    ///     <see cref="OccultCrescentHelper.IsAethernetUnlocked"/>.
    /// </summary>
    public static IEnumerable<AethernetData> EnumerateUsableAetherytes(this IZone zone) =>
        zone.GetAetherytes().Where(aetheryte => zone.IsUsableAethernetDestination(aetheryte.Id));

    /// <summary>True when Lifestream can teleport <i>to</i> this PlaceName (camp is always yes).</summary>
    public static bool IsUsableAethernetDestination(this IZone zone, uint placeNameId)
    {
        if (placeNameId == 0)
        {
            return false;
        }

        if (placeNameId == zone.GetMainAetheryte().Id)
        {
            return true;
        }

        return OccultCrescentHelper.IsAethernetUnlocked(placeNameId);
    }

    /// <summary>True when inside the magenta Lifestream ring (ready to teleport).</summary>
    public static bool IsWithinLifestreamRange(this IZone zone, Vector3 position)
    {
        return zone.EnumerateAetherytes()
            .Any(aetheryte => position.Distance2D(aetheryte.Position) <= aetheryte.GetLifestreamReadyRadius());
    }

    /// <summary>Idle can stop once at or inside the drawn cyan ring (no pad past it).</summary>
    public static bool IsWithinIdleWait(this IZone zone, Vector3 position)
    {
        return zone.EnumerateAetherytes()
            .Any(aetheryte => position.Distance2D(aetheryte.Position) <= aetheryte.GetIdleOuterRadius());
    }

    /// <summary>Idle wait spots on the approach side of the crystal (avoid walking around it).</summary>
    public static IEnumerable<Vector3> GetIdleWaitCandidates(this IZone zone, Vector3 from)
    {
        AethernetData? nearest = NearestAetheryte(zone, from);
        if (nearest == null)
        {
            yield break;
        }

        // Keep targets inside cyan after PathfindArrivalRadius so idle does not stop outside the ring.
        float inner = nearest.GetBodyRadius() + 0.25f;
        float outer = nearest.GetIdleOuterRadius() - PathfindArrivalRadius;
        if (outer <= inner)
        {
            outer = inner + MathF.Max(0.5f, EdgeClearance - PathfindArrivalRadius);
        }

        Vector3 crystal = nearest.Position;
        Vector3 approach = FlatOffset(from, crystal);
        if (approach.LengthSquared() < 0.25f)
        {
            approach = new Vector3(1f, 0f, 0f);
        }

        // Jitter the fan around the approach side so clients don't share one tile.
        float baseAngle = MathF.Atan2(approach.Z, approach.X)
                          + ((Random.Shared.NextSingle() * 2f - 1f) * (MathF.PI / 4f));
        const int steps = 5;
        for (int i = 0; i < steps; i++)
        {
            float bandT = ((i % 3) + 1) / 4f;
            float bandJitter = (Random.Shared.NextSingle() * 2f - 1f) * 0.08f;
            float radius = inner + ((outer - inner) * Math.Clamp(bandT + bandJitter, 0.05f, 1f));
            float angle = baseAngle
                          + ((i - (steps / 2)) * (MathF.PI / 6f))
                          + ((Random.Shared.NextSingle() * 2f - 1f) * (MathF.PI / 18f));
            // Offset is XZ-only — using crystal.Y here doubled altitude (259→519) and broke vnav.
            yield return crystal + new Vector3(
                MathF.Cos(angle) * radius,
                0f,
                MathF.Sin(angle) * radius);
        }
    }

    private static AethernetData? NearestAetheryte(IZone zone, Vector3 from) =>
        zone.EnumerateAetherytes()
            .OrderBy(aetheryte => from.Distance2D(aetheryte.Position))
            .FirstOrDefault();

    public static Vector3 ResolveInteractDestination(Vector3 destination, IZone zone, Vector3? from = null)
    {
        foreach (AethernetData aetheryte in zone.EnumerateAetherytes())
        {
            float toCrystal = destination.Distance2D(aetheryte.Position);
            float toDest = destination.Distance2D(aetheryte.GetInteractPosition());
            if (toCrystal <= aetheryte.GetIdleOuterRadius() + 2f || toDest <= 1.5f)
            {
                return aetheryte.GetCampStandOffPosition(from);
            }
        }

        return destination;
    }

    public static PathStep ResolveAetherytePathStep(IPathStep step, IZone zone, Vector3? from = null)
    {
        if (step is not PathStep pathStep || pathStep.PathStepData is not Pathfind(var destination, _))
        {
            return (PathStep)step;
        }

        Vector3 resolved = ResolveInteractDestination(destination, zone, from);
        if (resolved == destination)
        {
            return pathStep;
        }

        return PathStep.Pathfind(resolved, PathfindArrivalRadius);
    }

    public static AethernetData? FindAetheryte(this IZone zone, uint placeNameId)
    {
        return zone.EnumerateAetherytes()
            .FirstOrDefault(aetheryte => aetheryte.Id == placeNameId);
    }
}
