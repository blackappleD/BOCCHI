namespace BOCCHI.Services.Shopping;

public interface IShoppingService
{
    bool IsActive { get; }

    void ForceStop();

    /// <summary>
    ///     Debug / manual: Return to base camp if needed, then start the configured shopping backend
    ///     (GatherBuddy Reborn vendor list or Knightshopper). Works even when auto-shop is off. Returns false with a reason in <paramref name="detail"/>.
    /// </summary>
    bool TryForceStart(out string detail);

    /// <summary>One-line status for chat (phase, camp, backend run).</summary>
    string DescribeStatus();
}
