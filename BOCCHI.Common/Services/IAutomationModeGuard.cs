using BOCCHI.Common.Config;

namespace BOCCHI.Common.Services;

public enum AutomationMode
{
    None = 0,
    IllegalMode = 1,
    PotsAndTreasure = 2,
    MobFarmer = 3,
    TreasureHunt = 4,
    CarrotHunt = 5,
    Completionist = 6,
    Shopping = 7
}

public interface IAutomationModeGuard
{
    void EnsureExclusive(AutomationMode mode);

    void NotifyTreasureHuntEnded();

    /// <summary>A standalone Treasure Hunt / Carrot Hunt finished on its own — start the mode picked under "When hunt ends".</summary>
    void StartModeAfterHunt(HuntEndStartMode mode);

    void NotifyShoppingEnded();

    void EmergencyStop();
}
