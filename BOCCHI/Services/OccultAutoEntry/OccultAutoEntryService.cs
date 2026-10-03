using System.Numerics;
using BOCCHI.Automator.Services;
using BOCCHI.Common;
using BOCCHI.Common.Services;
using BOCCHI.Common.Config;
using BOCCHI.Common.Data.Zones;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Ocelot.Extensions;
using Ocelot.Ipc.VNavmesh;
using Ocelot.Lifecycle;
using Ocelot.Services.Logger;

namespace BOCCHI.Services.OccultAutoEntry;

/// <summary>
/// Re-enters Occult Crescent through Jeffroy in the Phantom Village after the instance timer kicks the player out.
/// A manual exit (time still on the clock) never arms it.
/// </summary>
public sealed unsafe class OccultAutoEntryService(
    UIConfig config,
    IClientState client,
    ICondition condition,
    IObjectTable objects,
    IDataManager data,
    IChatGui chat,
    IVNavmeshIpc vnav,
    IAutomator automator,
    ILogger<OccultAutoEntryService> logger
) : IOnUpdate, IOnTerritoryChanged
{
    public const ushort PhantomVillage = 1278;

    private const uint JeffroyBaseId = 1053611;

    private static readonly Vector3 JeffroyPosition = new(-77.9f, 5.0f, -15.4f);

    private const float InteractRadius = 4f;

    // Leaving the island with less than this left on the instance clock counts as "kicked by the timer".
    private const float TimeoutLeftSeconds = 120f;

    private static readonly TimeSpan ArrivalWindow = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan EntryTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    private float lastTimeLeft = float.MaxValue;

    private ushort lastTerritory;

    private DateTime armedAt = DateTime.MinValue;

    private DateTime arrivedAt = DateTime.MinValue;

    private DateTime startedAt = DateTime.MinValue;

    private string startedReason = string.Empty;

    private DateTime menuSeenAt = DateTime.MinValue;

    private static readonly TimeSpan MenuGrace = TimeSpan.FromSeconds(3);

    // Set when an auto-entry lands on the island; Illegal Mode starts once the island has loaded.
    private DateTime illegalModeDueAt = DateTime.MinValue;

    private static readonly TimeSpan IllegalModeDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan IllegalModeWindow = TimeSpan.FromMinutes(1);

    public OccultAutoEntryState State { get; private set; } = OccultAutoEntryState.Idle;

    public UpdateLimit UpdateLimit { get; } = new()
    {
        Mode = UpdateLimitMode.Milliseconds,
        Limit = 200,
    };

    public void OnTerritoryChanged(uint territory)
    {
        ushort previous = lastTerritory;
        lastTerritory = (ushort)territory;

        if (IsIsland(territory))
        {
            if (State == OccultAutoEntryState.Running)
            {
                logger.Info("[AutoEntry] Entered {Zone}; Illegal Mode starts shortly", (ZoneId)territory);
                illegalModeDueAt = DateTime.UtcNow + IllegalModeDelay;
            }

            Reset();
            return;
        }

        if (IsIsland(previous))
        {
            float left = lastTimeLeft;
            lastTimeLeft = float.MaxValue;

            bool timedOut = left <= TimeoutLeftSeconds;
            logger.Info(
                "[AutoEntry] Left {Zone} with {Left:0}s on the instance clock ({Kind}), auto-enter {Enabled}",
                (ZoneId)previous,
                left == float.MaxValue ? -1 : left,
                timedOut ? "timer" : "manual",
                config.AutoEnterAfterTimeout ? "on" : "off");

            if (timedOut && config.AutoEnterAfterTimeout)
            {
                State = OccultAutoEntryState.WaitingForVillage;
                armedAt = DateTime.UtcNow;
            }

            return;
        }

        if (State == OccultAutoEntryState.WaitingForVillage)
        {
            if (territory == PhantomVillage)
            {
                Begin("timer");
            }
            else if (DateTime.UtcNow - armedAt > ArrivalWindow)
            {
                logger.Info("[AutoEntry] Landed in {Territory} instead of the Phantom Village; cancelled", territory);
                Reset();
            }
        }
    }

    public void Update()
    {
        ushort territory = (ushort)client.TerritoryType;
        if (lastTerritory == 0)
        {
            lastTerritory = territory;
        }

        if (IsIsland(territory))
        {
            TrackTimeLeft();
            StartIllegalModeWhenReady();
            return;
        }

        illegalModeDueAt = DateTime.MinValue;

        if (State == OccultAutoEntryState.Idle)
        {
            return;
        }

        // Unticking the box drops a timer-triggered entry; "Enter now" runs regardless.
        if (!config.AutoEnterAfterTimeout && startedReason != "manual")
        {
            Cancel();
            return;
        }

        if (State == OccultAutoEntryState.WaitingForVillage)
        {
            return;
        }

        // A queue for a full island can be long; only time the walking/talking part.
        if (condition[ConditionFlag.InDutyQueue])
        {
            startedAt = DateTime.UtcNow;
        }

        if (DateTime.UtcNow - startedAt > EntryTimeout)
        {
            BocchiChat.PrintError(chat, config, "Auto-entry gave up after 10 minutes.");
            logger.Warning("[AutoEntry] Timed out");
            Stop();
            return;
        }

        if (territory != PhantomVillage)
        {
            return;
        }

        Tick();
    }

    public bool CanEnterNow => client.TerritoryType == PhantomVillage;

    /// <summary>Starts walking to Jeffroy now. Only works in the Phantom Village.</summary>
    public bool EnterNow()
    {
        if (client.TerritoryType != PhantomVillage)
        {
            return false;
        }

        Begin("manual");
        return true;
    }

    public void Cancel()
    {
        if (State == OccultAutoEntryState.Idle)
        {
            return;
        }

        logger.Info("[AutoEntry] Cancelled");
        Stop();
    }

    private void Begin(string reason)
    {
        startedReason = reason;
        State = OccultAutoEntryState.Running;
        arrivedAt = DateTime.UtcNow;
        startedAt = DateTime.UtcNow;
        logger.Info("[AutoEntry] Starting ({Reason}) → {Zone}", reason, config.AutoEnterZone);
    }

    private void Stop()
    {
        if (vnav.IsRunning())
        {
            vnav.Stop();
        }

        Reset();
    }

    private void Reset()
    {
        menuSeenAt = DateTime.MinValue;
        State = OccultAutoEntryState.Idle;
        startedReason = string.Empty;
    }

    private static bool IsIsland(uint territory) =>
        territory is (ushort)ZoneId.SouthHorn or (ushort)ZoneId.NorthHorn;

    private void StartIllegalModeWhenReady()
    {
        if (illegalModeDueAt == DateTime.MinValue || DateTime.UtcNow < illegalModeDueAt)
        {
            return;
        }

        if (DateTime.UtcNow - illegalModeDueAt > IllegalModeWindow)
        {
            logger.Warning("[AutoEntry] Island never finished loading; Illegal Mode not started");
            illegalModeDueAt = DateTime.MinValue;
            return;
        }

        if (objects.LocalPlayer == null
            || condition[ConditionFlag.BetweenAreas]
            || condition[ConditionFlag.BetweenAreas51]
            || !OccultCrescentHelper.IsStateAvailable())
        {
            return;
        }

        illegalModeDueAt = DateTime.MinValue;
        if (automator.IsIllegalMode)
        {
            return;
        }

        logger.Info("[AutoEntry] Starting Illegal Mode");
        automator.Toggle();
    }

    private void TrackTimeLeft()
    {
        PublicContentOccultCrescent* content = PublicContentOccultCrescent.GetInstance();
        if (content == null)
        {
            return;
        }

        float left = content->ContentTimeLeft;
        if (left > 0f)
        {
            lastTimeLeft = left;
        }
    }

    private void Tick()
    {
        if (objects.LocalPlayer is not { } player
            || condition[ConditionFlag.BetweenAreas]
            || condition[ConditionFlag.BetweenAreas51]
            || DateTime.UtcNow - arrivedAt < SettleDelay)
        {
            return;
        }

        if (TryHandleDutyPop() || TryHandleYesno() || TryHandleMenu() || TryHandleTalk())
        {
            return;
        }

        // Queued or already loading in: nothing to do but wait for the territory change.
        if (condition[ConditionFlag.BoundByDuty]
            || condition[ConditionFlag.BoundByDuty56]
            || condition[ConditionFlag.InDutyQueue]
            || condition[ConditionFlag.OccupiedInQuestEvent]
            || condition[ConditionFlag.OccupiedInEvent]
            || condition[ConditionFlag.Occupied39])
        {
            return;
        }

        IGameObject? jeffroy = objects.FirstOrDefault(o =>
            o is { ObjectKind: ObjectKind.EventNpc, IsTargetable: true } && o.BaseId == JeffroyBaseId);

        Vector3 target = jeffroy?.Position ?? JeffroyPosition;
        if (target.Distance2D(player.Position) > InteractRadius || jeffroy == null)
        {
            if (vnav.IsNavmeshReady() && !vnav.IsRunning() && EzThrottler.Throttle("AutoEntry::Path", 1500))
            {
                vnav.PathfindAndMoveCloseTo(target.GetApproachPosition(player.Position, 2.5f), false, 1.5f);
            }

            return;
        }

        if (vnav.IsRunning())
        {
            vnav.Stop();
        }

        if (EzThrottler.Throttle("AutoEntry::Interact", 1500))
        {
            logger.Debug("[AutoEntry] Talking to Jeffroy");
            TargetSystem.Instance()->InteractWithObject(
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)jeffroy.Address,
                false);
        }
    }

    private bool TryHandleMenu()
    {
        if (!GenericHelpers.TryGetAddonByName("SelectString", out AtkUnitBase* addon) || !GenericHelpers.IsAddonReady(addon))
        {
            return false;
        }

        if (menuSeenAt == DateTime.MinValue)
        {
            menuSeenAt = DateTime.UtcNow;
        }

        if (!EzThrottler.Throttle("AutoEntry::Menu", 750))
        {
            return true;
        }

        string wanted = Normalize(ContentName(config.AutoEnterZone));
        var menu = new AddonMaster.SelectString(addon);

        // "进入“…北征之章”" and "进入“…北征之章（两歧塔 超魔之塔）”" both contain the name; the shortest is the plain island.
        AddonMaster.SelectString.Entry? match = null;
        var matchLength = int.MaxValue;
        foreach (AddonMaster.SelectString.Entry entry in menu.Entries)
        {
            string text = Normalize(entry.Text);
            if (wanted.Length > 0 && text.Contains(wanted, StringComparison.OrdinalIgnoreCase) && text.Length < matchLength)
            {
                match = entry;
                matchLength = text.Length;
            }
        }

        if (match is { } chosen)
        {
            logger.Info("[AutoEntry] Menu → {Entry}", chosen.Text);
            chosen.Select();
            menuSeenAt = DateTime.MinValue;
            return true;
        }

        // Entry texts can lag a frame or two behind the addon becoming ready.
        if (DateTime.UtcNow - menuSeenAt < MenuGrace)
        {
            return true;
        }

        logger.Warning(
            "[AutoEntry] No menu entry for {Wanted}: {Entries}",
            wanted,
            string.Join(" | ", menu.Entries.Select(e => $"{e.Text} [{string.Join(' ', e.Text.Select(c => ((int)c).ToString("X4")))}]")));
        BocchiChat.PrintError(chat, config, $"Auto-entry: \"{wanted}\" is not in Jeffroy's menu. Stopped.");
        addon->FireCallbackInt(-1);
        Stop();
        return true;
    }

    private bool TryHandleYesno()
    {
        if (!GenericHelpers.TryGetAddonByName("SelectYesno", out AddonSelectYesno* yesno)
            || !GenericHelpers.IsAddonReady(&yesno->AtkUnitBase))
        {
            return false;
        }

        if (!EzThrottler.Throttle("AutoEntry::Yes", 750))
        {
            return true;
        }

        var master = new AddonMaster.SelectYesno((nint)yesno);
        string wanted = Normalize(ContentName(config.AutoEnterZone));
        if (wanted.Length > 0 && Normalize(master.Text).Contains(wanted, StringComparison.OrdinalIgnoreCase))
        {
            logger.Info("[AutoEntry] Confirming entry");
            master.Yes();
        }

        return true;
    }

    private bool TryHandleDutyPop()
    {
        if (!GenericHelpers.TryGetAddonByName("ContentsFinderConfirm", out AtkUnitBase* addon) || !GenericHelpers.IsAddonReady(addon))
        {
            return false;
        }

        if (EzThrottler.Throttle("AutoEntry::Commence", 1000))
        {
            logger.Info("[AutoEntry] Commence");
            new AddonMaster.ContentsFinderConfirm(addon).Commence();
        }

        return true;
    }

    private bool TryHandleTalk()
    {
        if (!GenericHelpers.TryGetAddonByName("Talk", out AtkUnitBase* addon) || !GenericHelpers.IsAddonReady(addon))
        {
            return false;
        }

        if (EzThrottler.Throttle("AutoEntry::Talk", 300))
        {
            new AddonMaster.Talk(addon).Click();
        }

        return true;
    }

    // The menu SeString doesn't render spaces/quotes the way the sheet stores them, so compare letters and digits only.
    private static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).ToArray());

    // Jeffroy's menu reads 进入“<content name>”, and the confirm prompt quotes the same name, in every client language.
    private string ContentName(ZoneId zone)
    {
        uint territory = (uint)zone;
        if (!data.GetExcelSheet<TerritoryType>().TryGetRow(territory, out TerritoryType row)
            || row.ContentFinderCondition.ValueNullable is not { } cfc)
        {
            return string.Empty;
        }

        return cfc.Name.ExtractText().Trim();
    }
}

public enum OccultAutoEntryState
{
    Idle,
    WaitingForVillage,
    Running,
}
