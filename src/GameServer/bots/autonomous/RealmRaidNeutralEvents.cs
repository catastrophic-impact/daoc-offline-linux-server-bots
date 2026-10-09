using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.Database;
using DOL.GS.ServerProperties;

namespace DOL.GS;

/// <summary>
/// Summoner's Hall and Darkness Falls as neutral realm events (owner, 2026-10-06): every realm can
/// run its own 200-300 bot expedition there, forced from the launcher or started automatically,
/// so two (rarely three) can be inside at once and fight each other, inside and on the way.
/// </summary>
public static class RealmRaidNeutralEvents
{
    public const ushort SummonersHall = 248;
    public const ushort DarknessFalls = 249;

    /// <summary>The dungeons that lead to Summoner's Hall: Hall of the Corrupt (Albion side),
    /// Dodens Gruva (Midgard side) and Marfach Caverns (Hibernia side).</summary>
    public static readonly ushort[] SummonersHallApproaches = [277, 246, 276];

    public static bool IsNeutralRegion(ushort region) => region is SummonersHall or DarknessFalls;

    // Off by default (owner, 2026-10-06): encounters keep their real levels. The run 4 skip that
    // prompted the cap was an engagement problem (too few raiders at the boss, nobody pulling),
    // and epic dungeons with level 61-80 monsters are cleared without any cap. New key, so the
    // old stored value (60) of neutral_raid_encounter_level is no longer read.
    [ServerProperty("autonomous", "neutral_raid_encounter_level_cap",
        "Optional: while a neutral raid (Summoner's Hall, Darkness Falls) is running there, its named encounters and epic monsters fight at no more than this level (0 = unchanged, the default).", 0)]
    public static int EncounterLevel = 0;

    /// <summary>Pure rule: the level a neutral-raid encounter fights at.</summary>
    public static byte CappedLevel(byte level, bool raidEncounter, int cap) =>
        raidEncounter && cap > 0 && level > cap ? (byte)cap : level;

    /// <summary>
    /// Level-75 summoners and level-64 to 80 sidhe and demons took ~6% damage in five minutes from
    /// 32 level-50 bots (2026-10-06): almost every attack missed or was resisted. During a raid the
    /// named encounters and epic monsters of the dungeon are capped; health, resists and mechanics
    /// are unchanged, and a respawn restores the original level.
    /// </summary>
    public static void ApplyEncounterLevels(ushort region, string[] objectives)
    {
        if (EncounterLevel <= 0 || !IsNeutralRegion(region)) return;
        foreach (GameNPC npc in WorldMgr.GetRegion(region)?.Objects.OfType<GameNPC>() ?? [])
        {
            if (npc is GameBot || !npc.IsAlive || npc.Realm != eRealm.None) continue;
            bool encounter = IsObjective(objectives, npc.Name) || npc is GameEpicNPC;
            byte level = CappedLevel(npc.Level, encounter, EncounterLevel);
            if (level != npc.Level) npc.Level = level;
        }
    }

    /// <summary>Regions where expedition bots of different realms fight each other on sight.</summary>
    public static bool IsBattleRegion(ushort region) =>
        IsNeutralRegion(region) || SummonersHallApproaches.Contains(region);

    public static ushort HomeRegion(eRealm realm) => realm switch
    {
        eRealm.Albion => 1,
        eRealm.Midgard => 100,
        eRealm.Hibernia => 200,
        _ => 0
    };

    /// <summary>Where the final boss is fought (its room), used as the route's last staging point.</summary>
    public static Vector3 FinalApproach(ushort region) => region switch
    {
        SummonersHall => new(32050, 40863, 15468),   // Grand Summoner Govannon
        DarknessFalls => new(45027, 51899, 15468),   // Legion
        _ => default
    };

    /// <summary>
    /// The named encounters, final boss last. Summoner's Hall: the three summoners, then Grand
    /// Summoner Govannon. Darkness Falls: the four High Lords, the three Princes, Princess Nahemah,
    /// then Legion. An encounter the raid cannot reach or damage is set aside by the route.
    /// </summary>
    public static string[] Objectives(ushort region) => region switch
    {
        SummonersHall => ["Summoner Cunovinda", "Summoner Roesia", "Summoner Lossren", "Grand Summoner Govannon"],
        DarknessFalls =>
        [
            "High Lord Baln", "High Lord Oro", "High Lord Saeor", "High Lord Baelerdoth",
            "Prince Asmoien", "Prince Abdin", "Prince Ba'alorien", "Princess Nahemah", "Legion"
        ],
        _ => null
    };

    /// <summary>
    /// Entrances on open ground first, so 38 parties can stage outside: Darkness Falls from Camelot
    /// Hills (81), the Vale of Mularn (84) and Connacht (87); the keep-tower entrances (Svasud Faste
    /// 82, Dun Lamfhota 86) only as a last resort. Unlisted entrances follow, nearest the hub first.
    /// </summary>
    public static int EntrancePreference(eRealm realm, int zonePointId)
    {
        int[] order = realm switch
        {
            eRealm.Albion => [81, 79, 88],
            eRealm.Midgard => [84, 83, 93, 82],
            eRealm.Hibernia => [87, 85, 94, 86],
            _ => []
        };
        int index = Array.IndexOf(order, zonePointId);
        return index < 0 ? order.Length : index;
    }

    public static bool IsObjective(string[] objectives, string name) =>
        objectives != null && objectives.Any(o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A realm's ways into a neutral dungeon from its home region: a direct zone point (Darkness
    /// Falls), or a zone point into an approach dungeon followed by that dungeon's zone point into
    /// the target (Summoner's Hall). Rows marked for another realm are skipped.
    /// </summary>
    public static IEnumerable<(DbZonePoint Outer, DbZonePoint Inner)> Entrances(IReadOnlyList<DbZonePoint> crossings,
        eRealm realm, ushort homeRegion, ushort target)
    {
        bool Usable(DbZonePoint p) => p.Realm == 0 || p.Realm == (ushort)realm;
        foreach (DbZonePoint direct in crossings.Where(p => Usable(p) && p.SourceRegion == homeRegion && p.TargetRegion == target))
            yield return (direct, direct);
        foreach (DbZonePoint outer in crossings.Where(p => Usable(p) && p.SourceRegion == homeRegion &&
                     SummonersHallApproaches.Contains(p.TargetRegion)))
            foreach (DbZonePoint inner in crossings.Where(p => Usable(p) && p.SourceRegion == outer.TargetRegion && p.TargetRegion == target))
                yield return (outer, inner);
    }
}
