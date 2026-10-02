using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>One common monster species of a faction's enemies in one outdoor zone.</summary>
    public sealed class FactionTargetCandidate
    {
        public string Name { get; init; }
        public int FactionId { get; init; }
        public ushort RegionId { get; init; }
        public ushort ZoneId { get; init; }
        public string ZoneName { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public int Z { get; init; }
        public byte TypicalLevel { get; init; }
        public byte MinLevel { get; init; }
        public byte MaxLevel { get; init; }
        public int SpawnCount { get; init; }

        /// <summary>
        /// Any living, no-realm monster of this name in the same outdoor region
        /// counts, whatever level its template rolled on this respawn.
        /// </summary>
        public bool Matches(GameNPC npc) =>
            npc != null && MatchesMonster(npc.Name, npc.Realm, npc.CurrentRegionID, npc.CurrentZone?.IsDungeon == true);

        public bool MatchesMonster(string name, eRealm realm, ushort regionId, bool inDungeon) =>
            realm == eRealm.None && !inDungeon && regionId == RegionId &&
            string.Equals(name, Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Chooses the kill target for a reputation quest: a common enemy of the
    /// faction, at the level nearest the player's without going over, capped
    /// at 45 so level-50 characters are never sent to level-50+ monsters.
    /// </summary>
    public static class FactionReputationTargets
    {
        public const int LevelCap = 45;
        public const int LevelWindow = 3;
        public const int MinimumSpawns = 5;

        private static readonly object CacheLock = new();
        private static readonly Dictionary<(int FactionId, ushort RegionId), IReadOnlyList<FactionTargetCandidate>> Cache = new();

        public static IReadOnlyList<FactionTargetCandidate> GetCandidates(int factionId, ushort regionId)
        {
            lock (CacheLock)
            {
                if (!Cache.TryGetValue((factionId, regionId), out var candidates))
                {
                    candidates = Build(factionId, regionId);
                    // Keep an empty result only once the world is up, so an
                    // early call cannot hide every target for the whole session.
                    if (candidates.Count > 0 || GameServer.Instance?.ServerStatus == EGameServerStatus.GSS_Open)
                        Cache[(factionId, regionId)] = candidates;
                }
                return candidates;
            }
        }

        /// <summary>
        /// The level nearest the player's without going over (and never above
        /// the cap), plus anything up to three levels below it for variety.
        /// A player below every target gets the lowest ones instead. A reroll
        /// excludes the previous species; a species with few spawns is used
        /// only when nothing better exists.
        /// </summary>
        public static FactionTargetCandidate[] SelectPool(IReadOnlyList<FactionTargetCandidate> candidates,
            int playerLevel, string excludedName)
        {
            if (candidates == null || candidates.Count == 0)
                return Array.Empty<FactionTargetCandidate>();

            var allowed = candidates.Where(candidate => string.IsNullOrWhiteSpace(excludedName) ||
                !string.Equals(candidate.Name, excludedName.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            var common = allowed.Where(candidate => candidate.SpawnCount >= MinimumSpawns).ToArray();
            if (common.Length > 0)
                allowed = common;
            if (allowed.Length == 0)
                return Array.Empty<FactionTargetCandidate>();

            int cap = Math.Min(Math.Max(playerLevel, 1), LevelCap);
            var atOrBelow = allowed.Where(candidate => candidate.TypicalLevel <= cap).ToArray();
            if (atOrBelow.Length > 0)
            {
                int best = atOrBelow.Max(candidate => candidate.TypicalLevel);
                return atOrBelow.Where(candidate => candidate.TypicalLevel >= best - LevelWindow).ToArray();
            }

            int lowest = allowed.Min(candidate => candidate.TypicalLevel);
            return allowed.Where(candidate => candidate.TypicalLevel <= lowest + LevelWindow).ToArray();
        }

        private static IReadOnlyList<FactionTargetCandidate> Build(int factionId, ushort regionId)
        {
            Faction faction = FactionMgr.GetFactionByID(factionId);
            Region region = WorldMgr.GetRegion(regionId);
            if (faction == null || region == null)
                return Array.Empty<FactionTargetCandidate>();

            HashSet<int> enemyIds = faction.EnemyFactions.Where(enemy => enemy != null && enemy.Id != factionId)
                .Select(enemy => enemy.Id).ToHashSet();
            if (enemyIds.Count == 0)
                return Array.Empty<FactionTargetCandidate>();

            var templates = GameServer.Database.SelectAllObjects<DbNpcTemplate>()
                .GroupBy(template => template.TemplateId)
                .ToDictionary(group => group.Key, group => group.First());

            var spawns = new List<(DbMob Mob, Zone Zone, int FactionId, byte[] Levels)>();
            foreach (DbMob mob in DOLDB<DbMob>.SelectObjects(DB.Column("Region").IsEqualTo(regionId)))
            {
                if (mob.ClassType != DbMob.DEFAULT_NPC_CLASSTYPE || mob.Realm != (byte)eRealm.None ||
                    mob.RespawnInterval <= 0 || string.IsNullOrWhiteSpace(mob.Name) ||
                    mob.Name != mob.Name.ToLowerInvariant() || string.IsNullOrWhiteSpace(mob.ObjectId))
                    continue;

                Zone zone = region.GetZone(mob.X, mob.Y);
                if (zone == null || zone.IsDungeon)
                    continue;

                templates.TryGetValue(mob.NPCTemplateID, out DbNpcTemplate template);
                bool templateOwns = template != null && template.ReplaceMobValues;
                int mobFaction = templateOwns ? template.FactionID : mob.FactionID;
                if (!enemyIds.Contains(mobFaction))
                    continue;

                byte[] levels = templateOwns && !string.IsNullOrWhiteSpace(template.Level)
                    ? Util.SplitCSV(template.Level, true).Select(value => byte.TryParse(value, out byte level) ? level : (byte)0)
                        .Where(level => level > 0).ToArray()
                    : new[] { mob.Level };
                if (levels.Length == 0)
                    levels = new[] { mob.Level };
                spawns.Add((mob, zone, mobFaction, levels));
            }

            return spawns.GroupBy(spawn => (spawn.Mob.Name, spawn.Zone.ID))
                .Select(group =>
                {
                    var members = group.ToArray();
                    double centerX = members.Average(spawn => spawn.Mob.X);
                    double centerY = members.Average(spawn => spawn.Mob.Y);
                    var representative = members
                        .OrderBy(spawn => Math.Abs(spawn.Mob.X - centerX) + Math.Abs(spawn.Mob.Y - centerY))
                        .ThenBy(spawn => spawn.Mob.ObjectId, StringComparer.Ordinal)
                        .First();
                    byte[] levels = members.SelectMany(spawn => spawn.Levels).OrderBy(level => level).ToArray();
                    return new FactionTargetCandidate
                    {
                        Name = representative.Mob.Name,
                        FactionId = representative.FactionId,
                        RegionId = regionId,
                        ZoneId = representative.Zone.ID,
                        ZoneName = representative.Zone.Description,
                        X = representative.Mob.X,
                        Y = representative.Mob.Y,
                        Z = representative.Mob.Z,
                        TypicalLevel = levels[levels.Length / 2],
                        MinLevel = levels[0],
                        MaxLevel = levels[^1],
                        SpawnCount = members.Length
                    };
                })
                .OrderBy(candidate => candidate.TypicalLevel)
                .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
