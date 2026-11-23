using BOCCHI.Common.Config;
using BOCCHI.Common.Data.Aethernet;
using BOCCHI.Common.Data.CriticalEncounters;
using BOCCHI.Common.Data.Fates;
using BOCCHI.Common.Data.Zones;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Ocelot.Lifecycle;
using Ocelot.Services.Logger;

namespace BOCCHI.Common.Services;

public sealed class AutoRevive(
    AutomatorConfig config,
    ICondition conditions,
    IObjectTable objects,
    IZoneProvider zones,
    ICriticalEncounterContext criticalEncounters,
    ICriticalEncounterRepository encounters,
    IFateContext fates,
    IFateRepository fateList,
    YesAlreadyPause yesAlready,
    ILogger<AutoRevive> logger
) : IOnUpdate, IOnStop
{
    private const uint RaisePendingStatusId = 148; // Raise

    private DateTime? deadSinceUtc;

    private bool diedInTower;

    private CriticalEncounterId? encounterAtDeath;

    private DateTime encounterSeenUtc;

    private FateId? fateAtDeath;

    private DateTime fateSeenUtc;

    private DateTime waitFromUtc;

    private bool sawAlive;

    private string? lastNote;

    // blackappleD fork: honour AutoAcceptRaiseDelaySeconds, but never let the offer run out.
    private const int RaiseOfferExpiryMarginSeconds = 5;

    private DateTime? raiseOfferSeenUtc;

    public unsafe void Update()
    {
        if (!zones.GetZone().IsOccultCrescentZone())
        {
            deadSinceUtc = null;
            sawAlive = false;
            lastNote = null;
            yesAlready.Release();
            return;
        }

        bool inTower = zones.GetZone().IsInForkedTower();
        CriticalEncounterId? encounter = criticalEncounters.GetRegisteredOrActiveCriticalEncounterId();
        FateId? fate = fates.GetFateId();
        if (!conditions[ConditionFlag.Unconscious])
        {
            deadSinceUtc = null;
            sawAlive = true;
            lastNote = null;
            raiseOfferSeenUtc = null;
            diedInTower = inTower;
            if (encounter != null)
            {
                encounterAtDeath = encounter;
                encounterSeenUtc = DateTime.UtcNow;
            }
            // The game can drop you from the CE a moment before you count as dead.
            else if (DateTime.UtcNow - encounterSeenUtc > TimeSpan.FromSeconds(10))
            {
                encounterAtDeath = null;
            }

            if (fate != null)
            {
                fateAtDeath = fate;
                fateSeenUtc = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - fateSeenUtc > TimeSpan.FromSeconds(10))
            {
                fateAtDeath = null;
            }

            yesAlready.Release();
            return;
        }

        // Event state may not survive death, so keep what was true while alive; a wrong release out of Forked Tower can't be undone.
        diedInTower |= inTower;
        encounterAtDeath ??= encounter;
        fateAtDeath ??= fate;
        if (deadSinceUtc == null)
        {
            deadSinceUtc = DateTime.UtcNow;
            logger.Info(
                "Auto revive: down (Forked Tower: {Tower}, critical encounter: {Encounter}, FATE: {Fate})",
                diedInTower,
                encounterAtDeath?.ToString() ?? "none",
                fateAtDeath?.ToString() ?? "none");
        }

        // Returning drops you out of the FATE or CE, so the wait for a raise only starts once it is over.
        bool activityRunning = EncounterStillRunning() || FateStillRunning();
        if (activityRunning || waitFromUtc < deadSinceUtc.Value)
        {
            waitFromUtc = DateTime.UtcNow;
        }

        if (!config.AutoAcceptRaise && !config.ReturnToBaseCampAfterDeath)
        {
            yesAlready.Release();
            return;
        }

        // A YesAlready rule would click the revive prompt first, also inside Forked Tower.
        yesAlready.Hold();

        bool promptOpen = AddonHelpers.TryGetSelectYesno(out AddonSelectYesno* yesno)
                          && !ReturnYesNo.IsPartyInvite(&yesno->AtkUnitBase);
        (AutoReviveAction action, string note) = AutoReviveRule.Decide(new AutoReviveState(
            config.AutoAcceptRaise,
            config.ReturnToBaseCampAfterDeath,
            config.ReturnAfterDeathDelaySeconds,
            promptOpen,
            objects.LocalPlayer?.StatusList.Any(s => s.StatusId == RaisePendingStatusId) == true,
            promptOpen && ReturnYesNo.IsReturnConfirmation(&yesno->AtkUnitBase),
            diedInTower,
            diedInTower || activityRunning,
            sawAlive,
            DateTime.UtcNow - waitFromUtc));

        bool raisePending = TryGetRaiseTimeLeft(out int raiseTimeLeft);
        if (!raisePending)
        {
            raiseOfferSeenUtc = null;
        }
        else
        {
            raiseOfferSeenUtc ??= DateTime.UtcNow;
        }

        if (action == AutoReviveAction.AcceptRaise && raisePending)
        {
            bool delayElapsed = DateTime.UtcNow - raiseOfferSeenUtc!.Value >= TimeSpan.FromSeconds(config.AutoAcceptRaiseDelaySeconds);
            if (!delayElapsed && raiseTimeLeft > RaiseOfferExpiryMarginSeconds)
            {
                action = AutoReviveAction.Nothing;
                note = $"accepting raise after {config.AutoAcceptRaiseDelaySeconds}s";
            }
        }

        if (action == AutoReviveAction.Nothing)
        {
            if (note != lastNote)
            {
                lastNote = note;
                logger.Info("Auto revive: {Note}", note);
            }

            return;
        }

        if (!EzThrottler.Throttle("AutoRevive::Yes", 1000))
        {
            return;
        }

        lastNote = null;
        logger.Info("Auto revive: {Reason}", note);
        new AddonMaster.SelectYesno((nint)yesno).Yes();
    }

    private bool EncounterStillRunning() =>
        encounterAtDeath is { } id
        && encounters.SnapshotWithoutForkedTower().Any(ce => ce.Id == id && (ce.IsPreparing() || ce.IsActive()));

    private bool FateStillRunning() => fateAtDeath is { } id && fateList.HasFate(id);

    public void OnStop() => yesAlready.Release();

    private static unsafe bool TryGetRaiseTimeLeft(out int timeLeft)
    {
        timeLeft = 0;
        AgentRevive* agent = AgentRevive.Instance();
        if (agent == null || agent->ResurrectingPlayerId == 0 || agent->ResurrectionTimeLeft <= 0)
        {
            return false;
        }

        timeLeft = agent->ResurrectionTimeLeft;
        return true;
    }
}
