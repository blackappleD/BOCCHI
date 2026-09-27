using BOCCHI.Common.Data.CriticalEncounters;
using BOCCHI.Common.Data.Fates;
using BOCCHI.Common.Data.Goals;
using BOCCHI.Common.Data.Paths;
using BOCCHI.Common.Data.SupportJobs;
using BOCCHI.Common.Data.Zones;
using BOCCHI.Common.Services.Paths;
using System.Numerics;

namespace BOCCHI.Common.Data.StateMemory;

public sealed class ApplyingBuffsMemory;

public sealed class ManualBuffRunMemory;

/// <summary>Inquiring Mind already ran this buff cycle — do not cast it again.</summary>
public sealed class InquiringMindAttemptedMemory;

public sealed class CastingTreasureSightMemory;

/// <summary>Post-FATE/CE: raise nearby players as Phantom Chemist before leaving.</summary>
public sealed class PendingTriageMemory;

/// <summary>Sticky while TriagingHandler is actively swapping/casting raises.</summary>
public sealed class TriagingMemory;

public sealed class TriageSupportJobMemory(SupportJobId job)
{
    public readonly SupportJobId Job = job;
}

/// <summary>
///     Post-activity Treasure Sight / map-hunt latch for Illegal Mode auto hunts.
/// </summary>
public sealed class AutomaticTreasureSurveyMemory
{
    /// <summary>Cast Sight when idle at base camp.</summary>
    public bool PendingSurvey { get; set; }

    /// <summary>Waiting for WideText after a Sight cast.</summary>
    public bool WaitingForSurveyResult { get; set; }

    /// <summary>
    ///     Start a built-in-map treasure hunt when idle (no Treasure Sight / Freelancer &lt; 10).
    ///     Does not block Choosing — a live FATE/CE can take priority first.
    /// </summary>
    public bool PendingMapHunt { get; set; }

    /// <summary>True while a Treasure Sight survey is latched or waiting for the chat result.</summary>
    public bool IsBusy => PendingSurvey || WaitingForSurveyResult;

    /// <summary>Accept surveys with Tracker.SurveyRevision &gt; this value.</summary>
    public int MinAcceptedRevision { get; set; }

    public DateTime SurveyWaitDeadlineUtc { get; set; }
}

public sealed class WaitingForCriticalEncounterMemory(CriticalEncounterId encounterId)
{
    public CriticalEncounterId EncounterId { get; } = encounterId;

    public bool IsFor(CriticalEncounterId id) => EncounterId == id;
}

/// <summary>
///     InCriticalEncounter already started for this CE. Keep the goal even if EventId / wait-ring
///     lag after you walk toward the boss (otherwise GoalValidator drops it and Wrath turns off).
/// </summary>
public sealed class CommittedCriticalEncounterMemory(CriticalEncounterId encounterId)
{
    public CriticalEncounterId EncounterId { get; } = encounterId;

    public bool IsFor(CriticalEncounterId id) => EncounterId == id;
}

/// <summary>
///     InFate already started for this FATE. Keep In FATE / combat AI if EventId drops after a
///     dodge or stepping out of the ring, but not after walking away from the FATE.
/// </summary>
public sealed class CommittedFateMemory(FateId fateId)
{
    public FateId FateId { get; } = fateId;

    public bool IsFor(FateId id) => FateId == id;
}

/// <summary>
///     In FATE/CE combat — block travel replan until the activity goal is dropped.
///     Avoids edge stutter when FATE sync flickers and Pathfinding fights BOCCHI AI.
/// </summary>
public sealed class SuspendTravelForActivityMemory;

/// <summary>
///     Arrived at predicted pot stand-off; hold until the FATE spawns.
/// </summary>
public sealed class WaitingForPotFateMemory;

/// <summary>
///     Pot FATE goal ended while the event was still up — start chest farm once it despawns.
/// </summary>
public sealed class PendingPotChestFarmMemory(FateId fateId)
{
    public FateId FateId { get; } = fateId;
}

/// <summary>
///     User / soft-cancel stopped navigation. Blocks auto-replan until the mode is toggled.
/// </summary>
public sealed class NavigationInterruptedMemory;

/// <summary>
///     Brief skip after route planning failed for a FATE/CE — Choosing picks something else
///     instead of hard-parking Illegal Mode on <see cref="NavigationInterruptedMemory"/>.
/// </summary>
public sealed class RouteUnreachableGoalMemory(IGoal goal, TimeSpan ttl)
{
    public IGoal Goal { get; } = goal;

    private readonly DateTimeOffset until = DateTimeOffset.UtcNow + ttl;

    public bool IsExpired => DateTimeOffset.UtcNow >= until;

    public bool MatchesCriticalEncounter(CriticalEncounterId id) =>
        !IsExpired && Goal.GoalType is CriticalEncounterGoal(var ce) && ce == id;

    public bool MatchesFate(FateId id) =>
        !IsExpired && Goal.GoalType is FateGoal(var fate) && fate == id;
}

/// <summary>Random idle at camp before the outbound teleport to a FATE/CE.</summary>
public sealed class BaseTeleportDelayMemory(TimeSpan delay)
{
    private readonly DateTime startedUtc = DateTime.UtcNow;

    public TimeSpan Delay { get; } = delay;

    public bool IsReady() => DateTime.UtcNow - startedUtc >= Delay;

    public TimeSpan Remaining()
    {
        TimeSpan left = Delay - (DateTime.UtcNow - startedUtc);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}

/// <summary>
///     One initial combat approach per FATE. Re-arms when the activity id changes.
/// </summary>
public sealed class InitialCombatApproachMemory<TActivityId>
    where TActivityId : struct
{
    private TActivityId? activityId;

    public bool IsPending { get; private set; }

    public void Track(TActivityId? currentActivityId)
    {
        if (Nullable.Equals(activityId, currentActivityId))
        {
            return;
        }

        activityId = currentActivityId;
        IsPending = currentActivityId.HasValue;
    }

    public void Complete()
    {
        IsPending = false;
    }
}

public sealed class GoalMemory(IGoal goal)
{
    public IGoal Goal
    {
        get => goal;
    }
}

public sealed class IdleStateMemory(TimeSpan returnAfter)
{
    public readonly DateTimeOffset Entered = DateTimeOffset.UtcNow;

    /// <summary>Rolled wait (2..max) before opportunistic Return while idle.</summary>
    public readonly TimeSpan ReturnAfter = returnAfter;

    public int ApproachCandidateIndex { get; set; }

    /// <summary>Shuffled cyan-ring wait spots for this idle session (avoids stacking on nearest).</summary>
    public List<Vector3>? WaitCandidates { get; set; }

    public TimeSpan GetIdleTime() => DateTimeOffset.UtcNow - Entered;

    public bool IsReadyToReturn() => GetIdleTime() >= ReturnAfter;
}

public sealed class ReturningStateMemory(TimeSpan castDelay)
{
    public readonly DateTimeOffset QueuedAt = DateTimeOffset.UtcNow;

    /// <summary>Rolled wait before casting Return (path handoff after FATE/CE). Zero when already waited while idle.</summary>
    public readonly TimeSpan CastDelay = castDelay;

    public TimeSpan GetTimeQueued() => DateTimeOffset.UtcNow - QueuedAt;

    public bool IsReadyToCast() => GetTimeQueued() >= CastDelay;
}

public class BuffSupportJobMemory(SupportJobId job)
{
    public readonly SupportJobId Job = job;
}

public class TreasureSightSupportJobMemory(SupportJobId job)
{
    public readonly SupportJobId Job = job;
}

public enum PotChestFarmMode
{
    /// <summary>Magical Elixir + compass hints (South Horn authored groups / North Horn binned spots).</summary>
    Smart,

    /// <summary>Visit authored positions (missing buff/elixir/hints, or rerolls).</summary>
    Blind,
}

public enum PotChestFarmPhase
{
    WaitingForBuff,
    ElixirAtCenter,
    SearchingCandidates,
    OpeningReveal,
    BlindSweep,
}

public sealed class PotChestFarmMemory
{
    private PotChestFarmMemory(
        FateId fateId,
        PotChestFarmMode mode,
        IEnumerable<Vector3> blindPositions)
    {
        FateId = fateId;
        Mode = mode;
        Chests = new Queue<Vector3>(blindPositions);
        BlindTotalChests = Chests.Count;
        Phase = mode == PotChestFarmMode.Smart
            ? PotChestFarmPhase.WaitingForBuff
            : PotChestFarmPhase.BlindSweep;
        PhaseStartedUtc = DateTimeOffset.UtcNow;
    }

    public static PotChestFarmMemory CreateSmart(FateId fateId) =>
        new(fateId, PotChestFarmMode.Smart, []);

    public static PotChestFarmMemory CreateBlind(FateId fateId, IEnumerable<Vector3> chestPositions) =>
        new(fateId, PotChestFarmMode.Blind, chestPositions);

    public FateId FateId { get; }

    public PotChestFarmMode Mode { get; private set; }

    public PotChestFarmPhase Phase { get; set; }


    public readonly Queue<Vector3> Chests;

    public int BlindTotalChests { get; private set; }

    public readonly Queue<PotTreasureCandidate> Candidates = new();

    public int CandidateTotal { get; set; }

    /// <summary>Every authored spot for this pot FATE — the set each hint narrows.</summary>
    public readonly List<PotTreasureCandidate> Pool = [];

    public DateTimeOffset PhaseStartedUtc { get; set; }

    public DateTimeOffset SettledAtUtc { get; set; } = DateTimeOffset.MinValue;

    public int ElixirAttempts { get; set; }

    public int HintRevisionBaseline { get; set; }

    /// <summary>
    ///     Where Magical Elixir was used for the pending compass reading. Hints must be applied from
    ///     this point, not from wherever we happen to be when the log is finally read (often mid-walk
    ///     or on the next pad after a travel chain blocked the handler).
    /// </summary>
    public Vector3? ElixirHintOrigin { get; set; }

    /// <summary>Hints already used to narrow the set — for logging how far in we are.</summary>
    public int HintsApplied { get; set; }

    /// <summary>
    ///     When Cache Me If You Can was first seen missing. Bounds the grace period in which an
    ///     already-revealed coffer still gets opened instead of abandoned.
    /// </summary>
    public DateTimeOffset BuffLostUtc { get; set; } = DateTimeOffset.MinValue;

    /// <summary>
    ///     Set once we start opening a coffer after the buff dropped. Keeps the farm alive for the
    ///     rest of the grace window so a reroll offer has somewhere to land — without it the farm is
    ///     forgotten the tick the chest opens and the reroll is lost.
    /// </summary>
    public bool HoldingAfterBuffLoss { get; set; }

    /// <summary>Set once the opened coffer disappears, so the reroll wait excludes the open itself.</summary>
    public bool RerollWaitStarted { get; set; }

    /// <summary>
    ///     True once a second-chance offer moved the search onto the reroll pads. Stops a repeated
    ///     offer message from re-seeding and discarding narrowing already done there.
    /// </summary>
    public bool OnRerollPool { get; set; }

    /// <summary>
    ///     True after opening at least one coffer this farm. Later search stays on second-chance
    ///     (reroll) pads only — never back to the pot FATE's own spots.
    /// </summary>
    public bool HasOpenedChest { get; set; }

    /// <summary>When we started waiting for the current (peek) blind chest to spawn.</summary>
    public DateTimeOffset WaitingForSpawnSince { get; set; } = DateTimeOffset.MinValue;

    public int RemainingChests => Mode == PotChestFarmMode.Smart
        ? (Phase is PotChestFarmPhase.SearchingCandidates or PotChestFarmPhase.OpeningReveal
            ? Candidates.Count
            : Math.Max(CandidateTotal, 1))
        : Chests.Count;

    public int TotalChests => Mode == PotChestFarmMode.Smart
        ? Math.Max(CandidateTotal, 1)
        : BlindTotalChests;

    public void BeginBlindFallback(IEnumerable<Vector3> positions)
    {
        Mode = PotChestFarmMode.Blind;
        Phase = PotChestFarmPhase.BlindSweep;
        Chests.Clear();
        foreach (Vector3 p in positions)
        {
            Chests.Enqueue(p);
        }

        BlindTotalChests = Chests.Count;
        Candidates.Clear();
        CandidateTotal = 0;
        ElixirAttempts = 0;
        ElixirHintOrigin = null;
        HintsApplied = 0;
        WaitingForSpawnSince = DateTimeOffset.MinValue;
        PhaseStartedUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Seed the full authored set for this pot FATE; hints narrow it from here.</summary>
    public void SeedPool(IEnumerable<PotTreasureCandidate> all)
    {
        Pool.Clear();
        Pool.AddRange(all);
    }

    /// <summary>Replace the live candidates with what survived the latest hint.</summary>
    public void NarrowTo(IEnumerable<PotTreasureCandidate> survivors)
    {
        Candidates.Clear();
        foreach (PotTreasureCandidate c in survivors)
        {
            Candidates.Enqueue(c);
        }

        CandidateTotal = Candidates.Count;
        HintsApplied++;
        ElixirAttempts = 0;
        ElixirHintOrigin = null;
        SettledAtUtc = DateTimeOffset.MinValue;
        Phase = PotChestFarmPhase.SearchingCandidates;
        PhaseStartedUtc = DateTimeOffset.UtcNow;
    }
}

public sealed class GoalPathStepMemory(IGoal goal, IPathCalculator calculator, bool pauseWhenPlanCompletes = false)
{
    private Task<PathCalculationResult>? pathStepTask = calculator.Calculate(goal);

    private bool emptyPlan;

    private bool routingFailed;

    /// <summary>When true, finishing the plan (or an empty teleport-only plan) pauses nav for manual travel.</summary>
    public bool PauseWhenPlanCompletes { get; } = pauseWhenPlanCompletes;

    public Queue<IPathStep> PathSteps { get; private set; } = [];

    /// <summary>Calc finished with zero steps (already at destination, or walks-only plan stripped).</summary>
    public bool IsEmptyPlan => emptyPlan && pathStepTask == null;

    /// <summary>Calc finished with no usable route while still far from the goal.</summary>
    public bool RoutingFailed => routingFailed && pathStepTask == null;

    public bool IsValid => pathStepTask != null || PathSteps.Count != 0 || emptyPlan || routingFailed;

    public void Update()
    {
        if (pathStepTask == null)
        {
            return;
        }

        if (!pathStepTask.IsCompleted)
        {
            return;
        }

        if (pathStepTask.IsCompletedSuccessfully)
        {
            PathCalculationResult result = pathStepTask.Result;
            PathSteps = result.Steps;
            routingFailed = result.RoutingFailed;
            emptyPlan = PathSteps.Count == 0 && !result.RoutingFailed;
        }
        else
        {
            routingFailed = true;
            emptyPlan = false;
            PathSteps = [];
        }

        pathStepTask = null;
    }

    public IPathStep? GetNextPathStep() => PathSteps.Count > 0 && PathSteps.TryPeek(out IPathStep? step) ? step : null;

    /// <summary>
    ///     True when the next hop is Teleport, or Pathfind→Teleport (walk to the pad).
    ///     Combat cancel must not abort that approach — it remounts across the map (#174).
    /// </summary>
    public bool IsApproachingAethernetTeleport()
    {
        if (PathSteps.Count == 0)
        {
            return false;
        }

        using IEnumerator<IPathStep> e = PathSteps.GetEnumerator();
        if (!e.MoveNext())
        {
            return false;
        }

        if (e.Current.Kind == PathStepKind.Teleport)
        {
            return true;
        }

        return e.Current.Kind == PathStepKind.Pathfind
               && e.MoveNext()
               && e.Current.Kind == PathStepKind.Teleport;
    }

    public void DequeuePathStep()
    {
        if (PathSteps.Any())
        {
            PathSteps.Dequeue();
        }
    }
}
