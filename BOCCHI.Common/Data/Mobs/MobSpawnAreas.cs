using BOCCHI.Common.Data.Zones;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System.Numerics;

namespace BOCCHI.Common.Data.Mobs;

/// <summary>
///     Where each always-up mob lives, as in-game map coordinates (the X/Y the map shows).
///     Source: Console Games Wiki Occult Crescent enemy tables. Weather / night spawns are
///     left out — they have no fixed area. Whole-number coords, so expect ~25y slop.
/// </summary>
public static class MobSpawnAreas
{
    private static readonly Dictionary<Mob, Vector2> MapCoordinates = new()
    {
        // South Horn
        [Mob.Aetherscab] = new(13f, 22f),
        [Mob.Apa] = new(5f, 8f),
        [Mob.Bachelor] = new(34f, 34f),
        [Mob.Bandersnatch] = new(36f, 7f),
        [Mob.Blackguard] = new(32f, 32f),
        [Mob.BloodDemon] = new(28f, 35f),
        [Mob.Brachiosaur] = new(15f, 27f),
        [Mob.Byblos] = new(7f, 25f),
        [Mob.Catoblepas] = new(23f, 20f),
        [Mob.Cetus] = new(22f, 7f),
        [Mob.Chaochu] = new(32f, 12f),
        [Mob.Chimera] = new(11f, 34f),
        [Mob.Claw] = new(13f, 31f),
        [Mob.Collagen] = new(8f, 28f),
        [Mob.DemonPawn] = new(30f, 36f),
        [Mob.Diplocaulus] = new(18f, 34f),
        [Mob.DirtyEye] = new(32f, 26f),
        [Mob.Echos] = new(22f, 33f),
        [Mob.Fan] = new(36f, 20f),
        [Mob.Flame] = new(8f, 9f),
        [Mob.FlyingLizard] = new(37f, 10f),
        [Mob.Foobar] = new(7f, 17f),
        [Mob.Foper] = new(16f, 28.5f),
        [Mob.Gaelicat] = new(37f, 12f),
        [Mob.Garula] = new(33f, 7f),
        [Mob.Garula2] = new(33f, 7f),
        [Mob.Golem] = new(12f, 8f),
        [Mob.Goobbue] = new(20f, 10f),
        [Mob.Goobbue2] = new(20f, 10f),
        [Mob.Haagenti] = new(4f, 19f),
        [Mob.Harpuia] = new(11f, 17f),
        [Mob.Headstone] = new(31f, 30f),
        [Mob.Headstone2] = new(31f, 30f),
        [Mob.Inkstain] = new(13f, 35f),
        [Mob.Isleblazer] = new(9f, 35f),
        [Mob.Karlabos] = new(23f, 10f),
        [Mob.Leshy] = new(28f, 12f),
        [Mob.LionStatant] = new(37f, 23f),
        [Mob.Marolith] = new(32f, 18f),
        [Mob.Meraevis] = new(25f, 22f),
        [Mob.Monk] = new(27f, 34f),
        [Mob.OccultGolem] = new(11f, 36f),
        [Mob.Panther] = new(29f, 29f),
        [Mob.Petalodite] = new(17f, 9f),
        [Mob.Rosebear] = new(16f, 19f),
        [Mob.Satana] = new(11f, 36f),
        [Mob.Sculpture] = new(36f, 25f),
        [Mob.Snapweed] = new(32f, 15f),
        [Mob.Taurus] = new(13f, 24f),
        [Mob.Taurus2] = new(13f, 24f),
        [Mob.Tormentor] = new(37f, 16f),
        [Mob.Triceratops] = new(27f, 26f),
        [Mob.Uragnite] = new(15f, 10f),
        [Mob.VoidViper] = new(14f, 33f),
        [Mob.VoidViper2] = new(14f, 33f),
        [Mob.Zaghnal] = new(8f, 9f),
        [Mob.Zangbeto] = new(6f, 16f),
        [Mob.Zaratan] = new(7f, 11f),
        [Mob.Zirnitra] = new(9f, 37f),
        // North Horn
        [Mob.Accursed] = new(39f, 4f),
        [Mob.Anila] = new(16f, 37f),
        [Mob.Arioch] = new(7f, 8f),
        [Mob.Banemite] = new(12f, 6f),
        [Mob.Belladonna] = new(20f, 8f),
        [Mob.Bibliotaph] = new(38f, 31f),
        [Mob.BigHorn] = new(6f, 27f),
        [Mob.Bile] = new(33f, 8f),
        [Mob.BirdOfTheCrescent] = new(9f, 39f),
        [Mob.Blackguard2] = new(20f, 20f),
        [Mob.Bombadeel] = new(27f, 22f),
        [Mob.Carrier] = new(10f, 4f),
        [Mob.Cingulata] = new(29f, 35f),
        [Mob.Cliffkite] = new(36f, 39f),
        [Mob.Coeurl] = new(15f, 39f),
        [Mob.Craklaw] = new(14f, 19f),
        [Mob.Dhara] = new(36f, 36f),
        [Mob.Elftoad] = new(22f, 36f),
        [Mob.Flame2] = new(5f, 36f),
        [Mob.Gargoyle] = new(18f, 22f),
        [Mob.Gazellehawk] = new(9f, 39f),
        [Mob.Gremlin] = new(7f, 13f),
        [Mob.Gusion] = new(23f, 25f),
        [Mob.Harpeia] = new(9f, 29f),
        [Mob.Haunt] = new(33f, 5f),
        [Mob.Hellhound] = new(23f, 18f),
        [Mob.Huwasi] = new(34f, 10f),
        [Mob.Jester] = new(24f, 23f),
        [Mob.Kaluk] = new(8f, 32f),
        [Mob.Kargas] = new(15f, 33f),
        [Mob.Lorelei] = new(39f, 22f),
        [Mob.Medusa] = new(22f, 30f),
        [Mob.Melia] = new(23f, 10f),
        [Mob.MossFungus] = new(13f, 8f),
        [Mob.Nanka] = new(37f, 7f),
        [Mob.OiseauRare] = new(5f, 10f),
        [Mob.Onion] = new(36f, 21f),
        [Mob.Opken] = new(24f, 14f),
        [Mob.Parthenope] = new(15f, 23f),
        [Mob.Ratel] = new(5f, 21f),
        [Mob.Regolith] = new(12f, 15f),
        [Mob.Rock] = new(14f, 15f),
        [Mob.RotEyes] = new(8f, 11f),
        [Mob.SaltSwallow] = new(34f, 15f),
        [Mob.SandSerpent] = new(17f, 31f),
        [Mob.Sapria] = new(24f, 12f),
        [Mob.Soblyn] = new(29f, 12f),
        [Mob.Stoneshell] = new(31f, 8f),
        [Mob.Succubus] = new(21f, 27f),
        [Mob.Tomato] = new(36f, 21f),
        [Mob.Urolith] = new(33f, 36f),
        [Mob.Vinegaroon] = new(32f, 23f),
        [Mob.Wamoura] = new(5f, 4f),
        [Mob.Weapon] = new(35f, 39f),
        [Mob.Woolback] = new(7f, 24f),
        [Mob.Worm] = new(32f, 30f),
        [Mob.Wraith] = new(19f, 5f),
        [Mob.Zirnitra2] = new(4f, 36f),
        [Mob.Zu] = new(30f, 29f),
    };

    public static bool TryGetMapCoordinate(Mob mob, out Vector2 mapCoordinate) =>
        MapCoordinates.TryGetValue(mob, out mapCoordinate);

    public static bool HasArea(Mob mob, ZoneId zone) =>
        MobData.GetZone(mob) == zone && MapCoordinates.ContainsKey(mob);

    /// <summary>World X/Z of the mob's area (Y is not known — snap it to the navmesh).</summary>
    public static bool TryGetWorldXZ(Mob mob, ZoneId zone, IDataManager data, out Vector2 worldXZ)
    {
        worldXZ = default;
        if (!HasArea(mob, zone)
            || !data.GetExcelSheet<TerritoryType>().TryGetRow((uint)zone, out TerritoryType territory)
            || !territory.Map.IsValid)
        {
            return false;
        }

        Map map = territory.Map.Value;
        Vector2 coordinate = MapCoordinates[mob];
        worldXZ = new(
            MapToWorld(coordinate.X, map.SizeFactor, map.OffsetX),
            MapToWorld(coordinate.Y, map.SizeFactor, map.OffsetY));
        return true;
    }

    // Inverse of the in-game map readout: map = 41 / c * ((world + offset) * c + 1024) / 2048 + 1.
    private static float MapToWorld(float mapCoordinate, ushort sizeFactor, short offset)
    {
        float c = sizeFactor / 100f;
        return (((mapCoordinate - 1f) * c / 41f * 2048f) - 1024f) / c - offset;
    }
}
