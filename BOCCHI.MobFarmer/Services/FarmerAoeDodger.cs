using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Ocelot.Extensions;
using Ocelot.Ipc.VNavmesh;
using Ocelot.Services.Logger;
using Ocelot.Services.Pathfinding;
using System.Numerics;
using System.Text.RegularExpressions;
using BattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace BOCCHI.MobFarmer.Services;

/// <summary>
///     Steps out of telegraphed enemy casts using the shape from the Action sheet. BossMod has no
///     modules for Occult Crescent field mobs, so its AI won't dodge them by itself.
/// </summary>
public sealed partial class FarmerAoeDodger(
    IObjectTable objects,
    IDataManager data,
    IPathfinder pathfinder,
    IVNavmeshIpc vnav,
    ILogger<FarmerAoeDodger> logger)
{
    private enum ShapeKind
    {
        Circle,
        Cone,
        Rect,
        Charge,
        Cross,
    }

    private readonly record struct AoeShape(
        ShapeKind Kind,
        float Range,
        float HalfWidth,
        float HalfAngle,
        bool AddHitbox,
        bool FixedLocation);

    private readonly record struct Threat(
        ShapeKind Kind,
        Vector2 Origin,
        Vector2 Direction,
        float Range,
        float HalfWidth,
        float HalfAngle)
    {
        public bool Contains(Vector3 point, float margin)
        {
            Vector2 offset = new Vector2(point.X, point.Z) - Origin;
            float distance = offset.Length();
            switch (Kind)
            {
                case ShapeKind.Circle:
                    return distance <= Range + margin;

                case ShapeKind.Cone:
                {
                    if (distance > Range + margin)
                    {
                        return false;
                    }

                    if (distance <= margin)
                    {
                        return true;
                    }

                    float cos = Vector2.Dot(offset / distance, Direction);
                    float angle = MathF.Acos(Math.Clamp(cos, -1f, 1f));
                    return angle <= HalfAngle + MathF.Atan2(margin, distance);
                }

                case ShapeKind.Rect:
                case ShapeKind.Charge:
                {
                    float along = Vector2.Dot(offset, Direction);
                    return along >= -margin && along <= Range + margin && Side(offset, Direction) <= HalfWidth + margin;
                }

                case ShapeKind.Cross:
                {
                    Vector2 perpendicular = new(-Direction.Y, Direction.X);
                    return InLine(offset, Direction, margin) || InLine(offset, perpendicular, margin);
                }

                default:
                    return false;
            }
        }

        private bool InLine(Vector2 offset, Vector2 direction, float margin) =>
            MathF.Abs(Vector2.Dot(offset, direction)) <= Range + margin
            && Side(offset, direction) <= HalfWidth + margin;

        private static float Side(Vector2 offset, Vector2 direction) =>
            MathF.Abs((offset.X * direction.Y) - (offset.Y * direction.X));
    }

    private const float ScanRange = 50f;

    // Room past the edge when picking a spot, and how far out still counts as safe once moving.
    private const float SafetyMargin = 1.5f;

    private const float ReleaseMargin = 0.5f;

    private const int Directions = 16;

    private const float DefaultConeDegrees = 90f;

    private const ulong InvalidObjectId = 0xE0000000;

    private static readonly float[] StepDistances = [2f, 3.5f, 5f, 7f, 9f, 12f, 15f, 20f, 25f];

    private readonly Dictionary<uint, AoeShape?> shapes = [];

    private Vector3? escape;

    public bool IsDodging => escape != null;

    /// <summary>Returns true while the dodger owns movement this frame.</summary>
    public bool Tick()
    {
        if (objects.LocalPlayer is not { } me || me.IsDead)
        {
            Reset();
            return false;
        }

        List<Threat> threats = CollectThreats(me);
        Vector3 position = me.Position;
        float hold = escape != null ? ReleaseMargin : 0f;
        if (!threats.Any(t => t.Contains(position, hold)))
        {
            if (escape != null)
            {
                logger.Debug("Mob Farmer: out of enemy AoE");
                Reset();
            }

            return false;
        }

        if (escape is { } current
            && vnav.IsRunning()
            && !threats.Any(t => t.Contains(current, ReleaseMargin)))
        {
            return true;
        }

        if (!TryFindEscape(position, threats, out Vector3 spot))
        {
            // Nowhere safe nearby — leave movement to the combat AI rather than freezing.
            Reset();
            return false;
        }

        escape = spot;
        pathfinder.Stop();
        vnav.Stop();
        vnav.FollowPath([spot], false);
        logger.Debug("Mob Farmer: dodging enemy AoE toward {Spot:F1}", spot);
        return true;
    }

    public void Reset()
    {
        if (escape != null)
        {
            vnav.Stop();
        }

        escape = null;
    }

    private unsafe List<Threat> CollectThreats(IPlayerCharacter me)
    {
        List<Threat> threats = [];
        foreach (IBattleNpc npc in objects.OfType<IBattleNpc>())
        {
            if (npc.BattleNpcKind != BattleNpcSubKind.Combatant
                || npc.IsDead
                || !npc.IsCasting
                || npc.CastActionType != 1
                || npc.Position.Distance2D(me.Position) > ScanRange
                || GetShape(npc.CastActionId) is not { } shape)
            {
                continue;
            }

            BattleChara* chara = (BattleChara*)npc.Address;
            if (chara == null)
            {
                continue;
            }

            Vector3 location = chara->CastInfo.TargetLocation;
            float rotation = chara->CastRotation;
            ulong targetId = npc.CastTargetObjectId;
            bool targetsSelf = targetId == npc.GameObjectId || targetId == 0 || targetId == InvalidObjectId;
            IGameObject? target = targetsSelf ? null : objects.SearchById(targetId);

            Vector2 casterXZ = new(npc.Position.X, npc.Position.Z);
            Vector2 direction = new(MathF.Sin(rotation), MathF.Cos(rotation));
            float hitbox = shape.AddHitbox ? npc.HitboxRadius : 0f;

            switch (shape.Kind)
            {
                case ShapeKind.Circle:
                {
                    Vector2 center;
                    if (shape.FixedLocation && location != Vector3.Zero)
                    {
                        center = new(location.X, location.Z);
                    }
                    else if (targetsSelf || target == null)
                    {
                        center = casterXZ;
                    }
                    else if (target.GameObjectId == me.GameObjectId)
                    {
                        // Follows us until it lands — moving can't get out of it.
                        continue;
                    }
                    else
                    {
                        center = new(target.Position.X, target.Position.Z);
                    }

                    threats.Add(new(ShapeKind.Circle, center, direction, shape.Range + hitbox, 0f, 0f));
                    break;
                }

                case ShapeKind.Charge:
                {
                    Vector3 end = location != Vector3.Zero ? location : target?.Position ?? npc.Position;
                    Vector2 toEnd = new Vector2(end.X, end.Z) - casterXZ;
                    float length = toEnd.Length();
                    if (length < 0.5f)
                    {
                        continue;
                    }

                    threats.Add(new(ShapeKind.Charge, casterXZ, toEnd / length, length, shape.HalfWidth, 0f));
                    break;
                }

                default:
                    threats.Add(new(shape.Kind, casterXZ, direction, shape.Range + hitbox, shape.HalfWidth, shape.HalfAngle));
                    break;
            }
        }

        return threats;
    }

    private bool TryFindEscape(Vector3 from, List<Threat> threats, out Vector3 spot)
    {
        // Search outward starting "away from the nearest threat", alternating left/right.
        Vector2 here = new(from.X, from.Z);
        Vector2 away = here - threats.OrderBy(t => Vector2.Distance(t.Origin, here)).First().Origin;
        float baseAngle = away.LengthSquared() > 0.01f ? MathF.Atan2(away.X, away.Y) : 0f;
        float step = MathF.Tau / Directions;

        foreach (float distance in StepDistances)
        {
            for (int i = 0; i < Directions; i++)
            {
                int offset = (i + 1) / 2 * (i % 2 == 0 ? -1 : 1);
                float angle = baseAngle + (offset * step);
                Vector3 candidate = new(
                    from.X + (MathF.Sin(angle) * distance),
                    from.Y,
                    from.Z + (MathF.Cos(angle) * distance));

                if (threats.Any(t => t.Contains(candidate, SafetyMargin)))
                {
                    continue;
                }

                if (!vnav.TryFindPointOnMesh(candidate, 1f, 4f, out Vector3 onMesh)
                    || onMesh.Distance2D(candidate) > 1f
                    || threats.Any(t => t.Contains(onMesh, ReleaseMargin)))
                {
                    continue;
                }

                spot = onMesh;
                return true;
            }
        }

        spot = default;
        return false;
    }

    private AoeShape? GetShape(uint actionId)
    {
        if (shapes.TryGetValue(actionId, out AoeShape? cached))
        {
            return cached;
        }

        AoeShape? shape = null;
        if (data.GetExcelSheet<LuminaAction>().TryGetRow(actionId, out LuminaAction row) && row.EffectRange > 0)
        {
            float range = row.EffectRange;
            float halfWidth = row.XAxisModifier / 2f;
            shape = row.CastType switch
            {
                2 or 6 or 7 => new AoeShape(ShapeKind.Circle, range, 0f, 0f, false, row.TargetArea || row.CastType == 7),
                5 => new AoeShape(ShapeKind.Circle, range, 0f, 0f, true, false),
                3 or 13 => new AoeShape(ShapeKind.Cone, range, 0f, ConeHalfAngle(row), true, false),
                4 or 12 when halfWidth > 0f => new AoeShape(ShapeKind.Rect, range, halfWidth, 0f, true, false),
                8 when halfWidth > 0f => new AoeShape(ShapeKind.Charge, range, halfWidth, 0f, false, true),
                11 when halfWidth > 0f => new AoeShape(ShapeKind.Cross, range, halfWidth, 0f, false, false),
                _ => null,
            };
        }

        shapes[actionId] = shape;
        return shape;
    }

    private static float ConeHalfAngle(LuminaAction row)
    {
        float degrees = DefaultConeDegrees;
        string path = row.Omen.ValueNullable?.Path.ExtractText() ?? string.Empty;
        Match match = FanAngle().Match(path);
        if (match.Success && float.TryParse(match.Groups[1].Value, out float parsed) && parsed is > 0f and <= 360f)
        {
            degrees = parsed;
        }

        return degrees * MathF.PI / 360f;
    }

    [GeneratedRegex(@"fan(\d{2,3})", RegexOptions.IgnoreCase)]
    private static partial Regex FanAngle();
}
