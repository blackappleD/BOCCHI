using System.Text.Json;
using Dalamud.Plugin;
using Ocelot.Services.Logger;

namespace BOCCHI.Automator.Services.TowerRoutes;

/// <summary>
///     Routes recorded by the player (plugin config dir, forked_tower_routes/*.json, newest first)
///     followed by the built-in ones.
/// </summary>
public sealed class TowerRouteStore(IDalamudPluginInterface plugin, ILogger<TowerRouteStore> logger)
{
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private List<TowerRoute> userRoutes = [];

    private DateTime nextReloadAt = DateTime.MinValue;

    public string Directory => Path.Combine(plugin.GetPluginConfigDirectory(), "forked_tower_routes");

    public IReadOnlyList<TowerRouteLeg> GetLegs(uint territory)
    {
        if (DateTime.UtcNow >= nextReloadAt)
        {
            Reload();
        }

        return userRoutes
            .Concat(BuiltInTowerRoutes.All)
            .Where(r => r.Territory == territory)
            .SelectMany(r => r.Legs)
            .Where(l => l.Points.Count >= 2)
            .ToList();
    }

    public string? Save(TowerRoute route)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string path = Path.Combine(Directory, $"{route.Territory}-{route.RecordedAt:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(route, JsonOptions));
            nextReloadAt = DateTime.MinValue;
            return path;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[TowerRoute] Could not save the route");
            return null;
        }
    }

    private void Reload()
    {
        nextReloadAt = DateTime.UtcNow + ReloadInterval;
        List<TowerRoute> loaded = [];
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                userRoutes = loaded;
                return;
            }

            foreach (string file in System.IO.Directory.GetFiles(Directory, "*.json"))
            {
                try
                {
                    TowerRoute? route = JsonSerializer.Deserialize<TowerRoute>(File.ReadAllText(file));
                    if (route != null)
                    {
                        loaded.Add(route);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn("[TowerRoute] Skipping unreadable route {File}: {Error}", file, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "[TowerRoute] Could not read the route folder");
        }

        userRoutes = loaded.OrderByDescending(r => r.RecordedAt).ToList();
    }
}
