using System.Numerics;
using BOCCHI.Automator.Data;
using BOCCHI.Automator.Services.Goals;
using BOCCHI.Common;
using BOCCHI.Common.Config;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Ocelot.Lifecycle;
using Ocelot.Services.Logger;
using Ocelot.Services.Translation;
using Ocelot.Windows;

namespace BOCCHI.Automator.Services.TowerRoutes;

/// <summary>
///     Records the route the player walks through the Forked Tower by hand (Illegal Mode off), one leg per
///     room, and saves it when they leave the tower. The navigator then walks those legs.
/// </summary>
public sealed class TowerRouteRecorder(
    IForkedTowerRegistration tower,
    IObjectTable objects,
    ICondition conditions,
    IClientState client,
    IAutomatorContext context,
    ForkedTowerConfig config,
    UIConfig uiConfig,
    TowerRouteStore store,
    IChatGui chat,
    ITranslator<MainWindow> translator,
    ILogger<TowerRouteRecorder> logger
) : IOnUpdate
{
    private const float MinPointSpacing = 1.5f;

    // Faster than running (or a one-tick jump) = a pad launched or teleported us.
    private const float LaunchSpeed = 11f;

    private const float TeleportJumpDistance = 30f;

    private const int MinPointsToSave = 10;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    private readonly List<List<TowerRoutePoint>> legs = [];

    private bool recording;

    private uint territory;

    private DateTime startedAt;

    private DateTime outsideSince = DateTime.MaxValue;

    private DateTime nextSampleAt;

    private Vector3? lastSample;

    private DateTime lastSampleAt;

    private bool gap;

    private bool airborne;

    public UpdateLimit UpdateLimit { get; } = new()
    {
        Mode = UpdateLimitMode.Milliseconds,
        Limit = 100,
    };

    public void Update()
    {
        bool wanted = config.RecordTowerRoute && !context.IsIllegalMode;
        bool inside = tower.IsInsideTower();
        DateTime now = DateTime.UtcNow;

        if (!recording)
        {
            if (wanted && inside)
            {
                Start(now);
            }

            return;
        }

        if (!wanted)
        {
            Finish(context.IsIllegalMode ? "Illegal Mode turned on" : "option turned off");
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

        if (now >= nextSampleAt)
        {
            nextSampleAt = now + SampleInterval;
            Sample(now);
        }
    }

    private void Start(DateTime now)
    {
        recording = true;
        territory = client.TerritoryType;
        startedAt = DateTime.Now;
        legs.Clear();
        legs.Add([]);
        outsideSince = DateTime.MaxValue;
        lastSample = null;
        gap = false;
        airborne = false;
        nextSampleAt = now;
        logger.Info("[TowerRoute] Recording the route through the tower (territory {Territory})", territory);
    }

    private void Sample(DateTime now)
    {
        IPlayerCharacter? me = objects.LocalPlayer;
        if (me == null
            || me.IsDead
            || conditions[ConditionFlag.BetweenAreas]
            || conditions[ConditionFlag.BetweenAreas51]
            || conditions[ConditionFlag.InCombat])
        {
            gap = true;
            return;
        }

        Vector3 position = me.Position;
        if (lastSample is { } previous)
        {
            float distance = Vector3.Distance(previous, position);
            float seconds = (float)(now - lastSampleAt).TotalSeconds;
            bool launched = distance > TeleportJumpDistance || (seconds > 0.05f && distance / seconds > LaunchSpeed && distance > 2f);
            lastSample = position;
            lastSampleAt = now;

            if (launched)
            {
                if (!airborne && legs[^1].Count > 0)
                {
                    legs.Add([]);
                }

                airborne = true;
                return;
            }

            airborne = false;
        }
        else
        {
            lastSample = position;
            lastSampleAt = now;
        }

        List<TowerRoutePoint> leg = legs[^1];
        if (leg.Count > 0 && Vector3.Distance(leg[^1].Position, position) < MinPointSpacing)
        {
            return;
        }

        leg.Add(new TowerRoutePoint(position.X, position.Y, position.Z, gap && leg.Count > 0));
        gap = false;
    }

    private void Finish(string reason)
    {
        recording = false;
        List<TowerRouteLeg> cleaned = legs.Select(Clean).Where(l => l != null).Select(l => l!).ToList();
        legs.Clear();

        int points = cleaned.Sum(l => l.Points.Count);
        if (points < MinPointsToSave)
        {
            logger.Info("[TowerRoute] Stopped recording ({Reason}); only {Points} points, nothing saved", reason, points);
            return;
        }

        string? path = store.Save(new TowerRoute
        {
            Territory = territory,
            Name = $"recorded {startedAt:yyyy-MM-dd HH:mm}",
            RecordedAt = startedAt,
            Legs = cleaned,
        });

        if (path == null)
        {
            return;
        }

        logger.Info("[TowerRoute] Saved {Legs} legs / {Points} points ({Reason}) to {Path}", cleaned.Count, points, reason, path);
        BocchiChat.Print(chat, uiConfig, translator.T(
            ".automation.automator.forked_tower_route_saved",
            ("legs", cleaned.Count),
            ("points", points),
            ("file", Path.GetFileName(path))));
    }

    // Drops the lift off a pad at the end and the drop onto the landing spot at the start (steeper than any
    // ramp), then legs too short to matter.
    private static TowerRouteLeg? Clean(List<TowerRoutePoint> points)
    {
        List<TowerRoutePoint> p = [..points];
        while (p.Count > 1 && IsSteep(p[^2], p[^1], rising: true))
        {
            p.RemoveAt(p.Count - 1);
        }

        while (p.Count > 1 && IsSteep(p[0], p[1], rising: false))
        {
            p.RemoveAt(0);
        }

        float length = 0f;
        for (int i = 1; i < p.Count; i++)
        {
            length += Vector3.Distance(p[i - 1].Position, p[i].Position);
        }

        if (p.Count < 2 || length < 5f)
        {
            return null;
        }

        p[0] = p[0] with { Pathfind = false };
        return new TowerRouteLeg { Points = p };
    }

    private static bool IsSteep(TowerRoutePoint from, TowerRoutePoint to, bool rising)
    {
        float climb = rising ? to.Y - from.Y : from.Y - to.Y;
        float flat = MathF.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Z - from.Z) * (to.Z - from.Z)));
        return climb > 0.5f && climb > flat;
    }
}
