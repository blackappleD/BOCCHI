using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using BOCCHI.Automator.Services.Goals;
using BOCCHI.Common.Config;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons.Hooks;
using ECommons.Hooks.ActionEffectTypes;
using Ocelot.Lifecycle;
using Ocelot.Services.Logger;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace BOCCHI.Automator.Services;

/// <summary>
///     Writes what happens around the player inside the Forked Tower (enemy actions, map effects, VFX,
///     object state, HP losses) to a file, so trap/laser timing can be worked out afterwards.
/// </summary>
public sealed unsafe class ForkedTowerRecorder(
    IForkedTowerRegistration tower,
    IObjectTable objects,
    IDalamudPluginInterface plugin,
    ForkedTowerConfig config,
    ILogger<ForkedTowerRecorder> logger
) : IOnUpdate, IOnStop, IDisposable
{
    private const float ObjectRange = 100f;

    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ObjectSampleInterval = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan PositionSampleInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly record struct Snapshot(
        uint BaseId,
        ObjectKind Kind,
        byte SubKind,
        Vector3 Position,
        float Rotation,
        bool Targetable,
        uint Visibility,
        uint CastId,
        bool Dead,
        string Statuses);

    private readonly Dictionary<ulong, Snapshot> known = [];

    private readonly Dictionary<nint, (string Path, long At)> liveVfx = [];

    private readonly Stopwatch clock = new();

    private StreamWriter? writer;

    private string path = string.Empty;

    private DateTime outsideSince = DateTime.MaxValue;

    private DateTime nextObjectSampleAt;

    private DateTime nextPositionSampleAt;

    private DateTime nextFlushAt;

    private Vector3 lastLoggedPosition;

    private uint lastHp;

    private bool actionEffectHooked;

    private bool mapEffectHooked;

    private bool vfxHooked;

    public bool IsRecording => writer != null;

    public void Update()
    {
        bool inside = config.RecordTowerMechanics && tower.IsInsideTower();
        DateTime now = DateTime.UtcNow;

        if (!IsRecording)
        {
            if (inside)
            {
                Start();
            }

            return;
        }

        if (!config.RecordTowerMechanics)
        {
            Finish("option turned off");
            return;
        }

        if (inside)
        {
            outsideSince = DateTime.MaxValue;
        }
        else if (outsideSince == DateTime.MaxValue)
        {
            outsideSince = now;
        }
        else if (now - outsideSince >= ExitGrace)
        {
            Finish("left the tower");
            return;
        }

        try
        {
            SamplePlayer(now);
            if (now >= nextObjectSampleAt)
            {
                nextObjectSampleAt = now + ObjectSampleInterval;
                SampleObjects();
            }

            if (now >= nextFlushAt)
            {
                nextFlushAt = now + FlushInterval;
                writer?.Flush();
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] Sampling failed — stopping the recorder");
            Finish("error");
        }
    }

    public void OnStop() => Dispose();

    public void Dispose() => Finish("plugin stopped");

    private void Start()
    {
        try
        {
            string dir = Path.Combine(plugin.GetPluginConfigDirectory(), "forked_tower_records");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.log");
            writer = new StreamWriter(path, false, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] Could not open a record file");
            writer = null;
            return;
        }

        clock.Restart();
        known.Clear();
        liveVfx.Clear();
        outsideSince = DateTime.MaxValue;
        lastHp = 0;
        lastLoggedPosition = Vector3.Zero;
        Write($"START {DateTime.Now:O}");
        logger.Info("[FTRec] Recording the Forked Tower to {Path}", path);
        Hook();
    }

    private void Finish(string reason)
    {
        if (writer == null)
        {
            return;
        }

        Unhook();
        Write($"END {reason}");
        try
        {
            writer.Dispose();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] Closing the record file failed");
        }

        writer = null;
        known.Clear();
        liveVfx.Clear();
        logger.Info("[FTRec] Stopped recording ({Reason}): {Path}", reason, path);
    }

    private void Hook()
    {
        try
        {
            ActionEffect.ActionEffectEvent += OnActionEffect;
            actionEffectHooked = true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] ActionEffect hook failed");
        }

        try
        {
            MapEffect.Init(OnMapEffect);
            mapEffectHooked = true;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] MapEffect hook failed");
        }

        try
        {
            StaticVfx.StaticVfxCreateEvent += OnStaticVfxCreate;
            StaticVfx.StaticVfxDtorEvent += OnVfxDtor;
            ActorVfx.ActorVfxCreateEvent += OnActorVfxCreate;
            ActorVfx.ActorVfxDtorEvent += OnVfxDtor;
            vfxHooked = true;
            StaticVfx.EnableCreate();
            StaticVfx.EnableDtor();
            ActorVfx.EnableCreate();
            ActorVfx.EnableDtor();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] VFX hooks failed");
        }

        Write($"HOOKS actionEffect={actionEffectHooked} mapEffect={mapEffectHooked} vfx={vfxHooked}");
    }

    private void Unhook()
    {
        if (actionEffectHooked)
        {
            ActionEffect.ActionEffectEvent -= OnActionEffect;
            actionEffectHooked = false;
        }

        if (mapEffectHooked)
        {
            TryRun(MapEffect.Dispose, "MapEffect dispose");
            mapEffectHooked = false;
        }

        if (vfxHooked)
        {
            StaticVfx.StaticVfxCreateEvent -= OnStaticVfxCreate;
            StaticVfx.StaticVfxDtorEvent -= OnVfxDtor;
            ActorVfx.ActorVfxCreateEvent -= OnActorVfxCreate;
            ActorVfx.ActorVfxDtorEvent -= OnVfxDtor;
            TryRun(StaticVfx.DisableCreate, "StaticVfx disable");
            TryRun(StaticVfx.DisableDtor, "StaticVfx disable");
            TryRun(ActorVfx.DisableCreate, "ActorVfx disable");
            TryRun(ActorVfx.DisableDtor, "ActorVfx disable");
            vfxHooked = false;
        }
    }

    private void TryRun(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] {What} failed", what);
        }
    }

    private void SamplePlayer(DateTime now)
    {
        IPlayerCharacter? me = objects.LocalPlayer;
        if (me == null)
        {
            return;
        }

        if (lastHp != 0 && me.CurrentHp < lastHp)
        {
            Write($"HP -{lastHp - me.CurrentHp} -> {me.CurrentHp}/{me.MaxHp} at {Format(me.Position)}");
        }

        lastHp = me.CurrentHp;

        if (now >= nextPositionSampleAt && Vector3.Distance(lastLoggedPosition, me.Position) > 0.5f)
        {
            nextPositionSampleAt = now + PositionSampleInterval;
            lastLoggedPosition = me.Position;
            Write($"POS {Format(me.Position)} rot {me.Rotation:F2}");
        }
    }

    private void SampleObjects()
    {
        IPlayerCharacter? me = objects.LocalPlayer;
        if (me == null)
        {
            return;
        }

        HashSet<ulong> seen = [];
        foreach (IGameObject obj in objects)
        {
            if (obj.ObjectKind == ObjectKind.Pc || Vector3.Distance(me.Position, obj.Position) > ObjectRange)
            {
                continue;
            }

            seen.Add(obj.GameObjectId);
            Snapshot now = Capture(obj);
            if (!known.TryGetValue(obj.GameObjectId, out Snapshot before))
            {
                known[obj.GameObjectId] = now;
                Write($"NEW {Describe(obj, now)}");
                continue;
            }

            string changes = Diff(before, now);
            if (changes.Length > 0)
            {
                known[obj.GameObjectId] = now;
                Write($"CHG {obj.GameObjectId:X} {now.BaseId} {changes}");
            }
        }

        foreach (ulong id in known.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            Write($"GONE {id:X} {known[id].BaseId}");
            known.Remove(id);
        }
    }

    private static Snapshot Capture(IGameObject obj)
    {
        var native = (CsGameObject*)obj.Address;
        uint castId = 0;
        string statuses = string.Empty;
        if (obj is IBattleChara chara)
        {
            castId = chara.IsCasting ? chara.CastActionId : 0;
            // Mob statuses churn with player DoTs; only untargetable helpers are interesting.
            if (!native->GetIsTargetable())
            {
                statuses = string.Join(",", chara.StatusList.Where(s => s.StatusId != 0).Select(s => s.StatusId));
            }
        }

        return new Snapshot(
            obj.BaseId,
            obj.ObjectKind,
            obj.SubKind,
            obj.Position,
            obj.Rotation,
            native->GetIsTargetable(),
            (uint)native->RenderFlags,
            castId,
            obj.IsDead,
            statuses);
    }

    private static string Diff(Snapshot before, Snapshot now)
    {
        StringBuilder sb = new();
        // Targetable mobs walk around all the time; their movement is noise.
        bool trackMovement = now.Kind != ObjectKind.BattleNpc || !now.Targetable;
        if (trackMovement && Vector3.Distance(before.Position, now.Position) > 1f)
        {
            sb.Append($" pos {Format(before.Position)}->{Format(now.Position)}");
        }

        if (trackMovement && MathF.Abs(before.Rotation - now.Rotation) > 0.2f)
        {
            sb.Append($" rot {before.Rotation:F2}->{now.Rotation:F2}");
        }

        if (before.Targetable != now.Targetable)
        {
            sb.Append($" targetable {before.Targetable}->{now.Targetable}");
        }

        if (before.Visibility != now.Visibility)
        {
            sb.Append($" vis 0x{before.Visibility:X}->0x{now.Visibility:X}");
        }

        if (before.CastId != now.CastId)
        {
            sb.Append($" cast {before.CastId}->{now.CastId}");
        }

        if (before.Dead != now.Dead)
        {
            sb.Append($" dead {before.Dead}->{now.Dead}");
        }

        if (before.Statuses != now.Statuses)
        {
            sb.Append($" status [{before.Statuses}]->[{now.Statuses}]");
        }

        return sb.ToString();
    }

    private static string Describe(IGameObject obj, Snapshot s) =>
        $"{obj.GameObjectId:X} {s.BaseId} {s.Kind}/{s.SubKind} \"{obj.Name}\" at {Format(s.Position)} rot {s.Rotation:F2} "
        + $"targetable {s.Targetable} vis 0x{s.Visibility:X} cast {s.CastId} dead {s.Dead} status [{s.Statuses}] "
        + $"hitbox {obj.HitboxRadius:F1}";

    private void OnActionEffect(ActionEffectSet set)
    {
        try
        {
            if (writer == null || set.Source is { ObjectKind: ObjectKind.Pc })
            {
                return;
            }

            ulong me = objects.LocalPlayer?.GameObjectId ?? 0;
            uint damageToMe = 0;
            bool hitMe = false;
            foreach (TargetEffect target in set.TargetEffects)
            {
                if (target.TargetID != me)
                {
                    continue;
                }

                hitMe = true;
                target.ForEach(entry =>
                {
                    if (entry.type == ActionEffectType.Damage)
                    {
                        damageToMe += entry.Damage;
                    }
                });
            }

            IGameObject? source = set.Source;
            string sourceText = source == null
                ? "?"
                : $"{source.GameObjectId:X} {source.BaseId} \"{source.Name}\" at {Format(source.Position)} rot {source.Rotation:F2}";
            Write(
                $"ACT {set.Header.ActionID} \"{set.Name}\" anim {set.Header.AnimationId} from {sourceText} pos {Format(set.Position)} "
                + $"hdrRot {set.Header.Rotation} targets {set.Header.TargetCount} hitMe {hitMe} dmg {damageToMe}");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] ActionEffect logging failed");
        }
    }

    private void OnMapEffect(long director, uint position, ushort data1, ushort data2)
    {
        try
        {
            Write($"MAP pos {position} data {data1} {data2} director 0x{director:X}");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] MapEffect logging failed");
        }
    }

    private void OnStaticVfxCreate(nint vfx, string vfxPath, string systemSource)
    {
        try
        {
            liveVfx[vfx] = (vfxPath, clock.ElapsedMilliseconds);
            Write($"SVFX+ {vfx:X} {vfxPath} src {systemSource}");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] Static VFX logging failed");
        }
    }

    private void OnActorVfxCreate(nint vfx, nint vfxPath, nint caster, nint target, float a4, byte a5, ushort a6, byte a7)
    {
        try
        {
            IGameObject? casterObject = caster == 0 ? null : objects.CreateObjectReference(caster);
            if (casterObject is { ObjectKind: ObjectKind.Pc })
            {
                return;
            }

            IGameObject? targetObject = target == 0 ? null : objects.CreateObjectReference(target);
            string pathText = Marshal.PtrToStringAnsi(vfxPath) ?? "?";
            liveVfx[vfx] = (pathText, clock.ElapsedMilliseconds);
            string casterText = casterObject == null ? "?" : $"{casterObject.BaseId} at {Format(casterObject.Position)} rot {casterObject.Rotation:F2}";
            string targetText = targetObject == null ? "?" : $"{targetObject.ObjectKind} {targetObject.BaseId} at {Format(targetObject.Position)}";
            Write($"AVFX+ {vfx:X} {pathText} caster {casterText} target {targetText}");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] Actor VFX logging failed");
        }
    }

    private void OnVfxDtor(nint vfx)
    {
        try
        {
            if (liveVfx.Remove(vfx, out (string Path, long At) created))
            {
                Write($"VFX- {vfx:X} {created.Path} after {clock.ElapsedMilliseconds - created.At}ms");
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[FTRec] VFX dtor logging failed");
        }
    }

    private void Write(string line)
    {
        writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} +{clock.ElapsedMilliseconds,8} {line}");
    }

    private static string Format(Vector3 v) => $"({v.X:F1}, {v.Y:F1}, {v.Z:F1})";
}
