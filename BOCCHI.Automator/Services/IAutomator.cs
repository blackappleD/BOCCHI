using BOCCHI.Automator.Data;

namespace BOCCHI.Automator.Services;

public interface IAutomator
{
    bool Enabled { get; }

    bool IsActive { get; }

    bool IsPotsAndTreasure { get; }

    bool IsCompletionist { get; }

    bool SuspendedForTreasure { get; }

    bool SuspendedForShopping { get; }

    /// <summary>Illegal Mode is inside the Forked Tower and only fighting; nothing else may start.</summary>
    bool FightingInForkedTower { get; }

    bool IsIllegalMode { get; }

    void SetSuspendedForTreasure(bool suspended);

    void SetSuspendedForShopping(bool suspended);

    void SoftStopPathfinding();

    AutomatorState? CurrentState { get; }

    void Toggle();

    void TogglePotsAndTreasure();

    void ToggleCompletionist();

    void RefreshPathfinding();

    void RebuildPathMap();

    void Render();
}
