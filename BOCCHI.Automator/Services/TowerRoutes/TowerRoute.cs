using System.Numerics;
using System.Text.Json.Serialization;

namespace BOCCHI.Automator.Services.TowerRoutes;

/// <summary>
///     A point on a recorded route. <see cref="Pathfind" /> marks a point reached after a gap in the
///     recording (combat, a detour), so the stretch leading to it is pathfound instead of walked straight.
/// </summary>
public sealed record TowerRoutePoint(float X, float Y, float Z, bool Pathfind = false)
{
    [JsonIgnore]
    public Vector3 Position => new(X, Y, Z);
}

/// <summary>One room's walk: from where you land to the pad (or trigger) that takes you to the next room.</summary>
public sealed class TowerRouteLeg
{
    public List<TowerRoutePoint> Points { get; set; } = [];
}

public sealed class TowerRoute
{
    public uint Territory { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime RecordedAt { get; set; }

    public List<TowerRouteLeg> Legs { get; set; } = [];
}
