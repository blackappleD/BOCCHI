using System.Numerics;

namespace BOCCHI.Common.Services;

public interface IActivityNavigation
{
    bool CanPathfind { get; }

    /// <summary>True while the most recent PathTo/PathToPoint/TeleportToward request is still routing or running.</summary>
    bool IsNavigating { get; }

    bool CanTeleport(Vector3 destination, out string? disabledReason);

    void PathTo(Vector3 destination, string name, string id);

    void PathToPoint(Vector3 destination, string name, string id);

    void TeleportToward(Vector3 destination, string name, string id);

    void Cancel();
}
