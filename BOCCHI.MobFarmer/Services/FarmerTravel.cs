using BOCCHI.Common.Services;
using System.Numerics;

namespace BOCCHI.MobFarmer.Services;

/// <summary>
///     Long walks to a farm spot go through <see cref="IActivityNavigation" /> so they get the
///     aethernet hop and auto-mount. Tracks whether the farmer started the current request so we
///     only cancel our own navigation.
/// </summary>
public sealed class FarmerTravel(IActivityNavigation navigation)
{
    private const string NavigationId = "MobFarmer";

    public Vector3? Destination { get; private set; }

    public bool CanStart => navigation.CanPathfind;

    public bool IsActive => Destination != null && navigation.IsNavigating;

    public void Start(Vector3 destination, string name)
    {
        Destination = destination;
        navigation.PathToPoint(destination, name, NavigationId);
    }

    public void Cancel()
    {
        if (IsActive)
        {
            navigation.Cancel();
        }

        Destination = null;
    }

    public void Forget() => Destination = null;
}
