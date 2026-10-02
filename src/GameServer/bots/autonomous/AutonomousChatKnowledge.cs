using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.GS.Movement;

namespace DOL.GS;

/// <summary>
/// Read-only Classic/SI knowledge used by bot chat. World locations are indexed
/// once, on the first actual player question; spell questions read the player's
/// own class lines. Nothing runs on an AI tick and no external service is used.
/// </summary>
public static class AutonomousChatKnowledge
{
    private sealed record Place(string Name, string Area, ushort RegionId, bool IsDungeon, bool IsMonster, byte Level);

    private static readonly object IndexSync = new();
    private static IReadOnlyDictionary<string, Place[]> _places;

    public static bool TryAnswer(GamePlayer player, string message, out string answer)
    {
        answer = string.Empty;
        if (player == null || string.IsNullOrWhiteSpace(message))
            return false;

        string query = message.Trim();
        if (LooksLikeStableQuestion(query) && TryAnswerStable(player.Realm, query, out answer))
            return true;
        if (LooksLikeSpellQuestion(query) && TryAnswerSpell(player, query, out answer))
            return true;
        return LooksLikeLocationQuestion(query) && TryAnswerLocation(player.Realm, query, out answer);
    }

    public static bool TryAnswer(eRealm realm, string message, out string answer)
    {
        answer = string.Empty;
        if (string.IsNullOrWhiteSpace(message))
            return false;
        string query = message.Trim();
        if (LooksLikeStableQuestion(query) && TryAnswerStable(realm, query, out answer))
            return true;
        return LooksLikeLocationQuestion(query) && TryAnswerLocation(realm, query, out answer);
    }

    private static bool TryAnswerStable(eRealm realm, string query, out string answer)
    {
        answer = string.Empty;
        var routes = new List<(string Master, string Area, string Destination)>();
        foreach (Region region in WorldMgr.GetAllRegions().Where(region => region != null &&
                     AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(region.Expansion) &&
                     RegionIsAccessible(realm, region.ID)))
        {
            foreach (GameStableMaster master in region.Objects.OfType<GameStableMaster>()
                         .Where(master => master.ObjectState is GameObject.eObjectState.Active &&
                                          (master.Realm == realm || master.Realm == eRealm.None) && master.TradeItems != null))
            {
                foreach (DictionaryEntry entry in master.TradeItems.GetAllItems())
                {
                    if (entry.Value is not DbItemTemplate ticket || ticket.Item_Type != 40)
                        continue;
                    string destination = TicketDestination(ticket.Name);
                    PathPoint path = MovementMgr.LoadPath(ticket.Id_nb);
                    PathPoint endpoint = path;
                    int guard = 0;
                    while (endpoint?.Next != null && guard++ < 5000)
                        endpoint = endpoint.Next;
                    string endpointArea = endpoint == null ? string.Empty : region.GetZone(endpoint.X, endpoint.Y)?.Description ?? string.Empty;
                    if (!ContainsPhrase(query, destination) && !ContainsPhrase(query, endpointArea))
                        continue;
                    routes.Add((master.Name, master.CurrentZone?.Description ?? region.Description,
                        string.IsNullOrWhiteSpace(endpointArea) ? destination : endpointArea));
                }
            }
        }

        (string Master, string Area, string Destination)[] matches = routes.Distinct().Take(3).ToArray();
        if (matches.Length == 0)
            return false;
        string masters = string.Join("; ", matches.Select(route => $"{route.Master} in {route.Area}"));
        answer = $"For {matches[0].Destination}, buy the ticket from {masters}. Check the destination on the ticket before departing.";
        return true;
    }

    private static bool TryAnswerSpell(GamePlayer player, string query, out string answer)
    {
        answer = string.Empty;
        var candidates = new List<(Spell Spell, SpellLine Line)>();
        foreach (Specialization specialization in player.GetSpecList())
        {
            foreach (SpellLine line in specialization.PretendSpellLinesForLiving(player, 50))
            {
                foreach (Spell spell in SkillBase.GetSpellList(line.KeyName))
                {
                    if (spell?.Level is < 1 or > 50 || string.IsNullOrWhiteSpace(spell.Name) ||
                        !ContainsPhrase(query, spell.Name))
                        continue;
                    candidates.Add((spell, line));
                }
            }
        }

        (Spell Spell, SpellLine Line) match = candidates
            .DistinctBy(entry => (entry.Spell.Name.ToLowerInvariant(), entry.Spell.Level, entry.Line.KeyName))
            .OrderByDescending(entry => entry.Spell.Name.Length)
            .ThenBy(entry => entry.Spell.Level)
            .FirstOrDefault();
        if (match.Spell == null)
            return false;

        answer = match.Line.IsBaseLine
            ? $"{match.Spell.Name} is learned at character level {match.Spell.Level} in {match.Line.Name}. Check with your trainer after leveling."
            : $"{match.Spell.Name} needs {match.Spell.Level} in {match.Line.Name}. Train that specialization to unlock it.";
        return true;
    }

    private static bool TryAnswerLocation(eRealm realm, string query, out string answer)
    {
        answer = string.Empty;
        IReadOnlyDictionary<string, Place[]> index = GetPlaces();
        KeyValuePair<string, Place[]> match = index
            .Where(entry => entry.Key.Length >= 3 && ContainsPhrase(query, entry.Key))
            .OrderByDescending(entry => entry.Key.Length)
            .FirstOrDefault();
        if (match.Value == null)
            return false;

        Place[] reachable = match.Value.Where(place => RegionIsAccessible(realm, place.RegionId)).ToArray();
        if (reachable.Length == 0)
            return false;
        Place place = reachable
            .GroupBy(entry => (entry.Area, entry.RegionId, entry.IsDungeon, entry.IsMonster))
            .OrderByDescending(group => group.Count())
            .Select(group => group.First())
            .First();

        if (place.IsMonster)
        {
            answer = place.IsDungeon
                ? $"{place.Name} is inside {place.Area}. Bring a group and clear each aggressive pull on the way in."
                : $"You can find {place.Name} around {place.Area}. Ask around there if you need more precise directions.";
            return true;
        }

        if (place.IsDungeon)
        {
            DbZonePoint entrance = DOLDB<DbZonePoint>.SelectObjects(DB.Column("TargetRegion").IsEqualTo(place.RegionId))
                .Where(point => point.SourceRegion != 0 && (point.Realm == 0 || point.Realm == (ushort)realm))
                .FirstOrDefault(point => RegionIsAccessible(realm, point.SourceRegion));
            if (entrance != null)
            {
                Region source = WorldMgr.GetRegion(entrance.SourceRegion);
                string sourceArea = source?.GetZone(entrance.SourceX, entrance.SourceY)?.Description ?? source?.Description;
                if (!string.IsNullOrWhiteSpace(sourceArea))
                {
                    answer = $"The {realm} entrance to {place.Name} is in {sourceArea}. Travel together and clear the approach carefully.";
                    return true;
                }
            }
            answer = $"{place.Name} is a dungeon reached from your realm side. Find the marked entrance and bring a suitable group.";
            return true;
        }

        answer = place.Area.Equals(place.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{place.Name} is its own city area in your realm. Ask a stable master if you are far away."
            : $"{place.Name} is in {place.Area}. Follow the road signs or use a stable master for part of the trip.";
        return true;
    }

    private static IReadOnlyDictionary<string, Place[]> GetPlaces()
    {
        if (_places != null)
            return _places;
        lock (IndexSync)
        {
            if (_places != null)
                return _places;

            var building = new Dictionary<string, List<Place>>(StringComparer.OrdinalIgnoreCase);
            foreach (Region region in WorldMgr.GetAllRegions().Where(region => region != null &&
                         AutonomousCapnBryGoalCatalog.IsClassicOrShroudedIslesExpansion(region.Expansion)))
            {
                Add(building, region.Description, new(region.Description, region.Description, region.ID, region.IsDungeon, false, 0));
                foreach (Zone zone in region.Zones.Where(zone => zone != null))
                    Add(building, zone.Description, new(zone.Description, region.Description, region.ID, zone.IsDungeon, false, 0));

                foreach (GameNPC npc in region.Objects.OfType<GameNPC>())
                {
                    if (npc?.ObjectState is not GameObject.eObjectState.Active || string.IsNullOrWhiteSpace(npc.Name) || npc.CurrentZone == null)
                        continue;
                    bool monster = npc.Realm == eRealm.None && npc.Level > 0 && npc.Brain != null &&
                                   (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) == 0 &&
                                   npc is not GameMerchant and not GameGuard and not GameTaxi and not GameSummonedPet and
                                   not GameTrainer and not GameTeleporter and not GameHealer and not CraftNPC;
                    Add(building, npc.Name, new(npc.Name, npc.CurrentZone.Description, region.ID,
                        npc.CurrentZone.IsDungeon || region.IsDungeon, monster, npc.Level));
                }
            }
            _places = building.ToDictionary(entry => entry.Key, entry => entry.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
            return _places;
        }
    }

    private static void Add(Dictionary<string, List<Place>> index, string key, Place place)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        if (!index.TryGetValue(key.Trim(), out List<Place> places))
            index[key.Trim()] = places = new();
        places.Add(place);
    }

    private static bool LooksLikeLocationQuestion(string text) =>
        text.Contains("where", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("find", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("found", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("location", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("located", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("how do i get", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeStableQuestion(string text) =>
        text.Contains("stable master", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("stablemaster", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("horse to", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("ticket to", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSpellQuestion(string text) =>
        text.Contains("what level", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("which level", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("when do i learn", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("when can i cast", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("learn to cast", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsPhrase(string text, string phrase) =>
        !string.IsNullOrWhiteSpace(phrase) && text.Contains(phrase, StringComparison.OrdinalIgnoreCase);

    private static string TicketDestination(string name)
    {
        string value = name?.Trim() ?? string.Empty;
        int marker = value.IndexOf("ticket to", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? value[(marker + "ticket to".Length)..].Trim() : value;
    }

    // Matches the authoritative Classic/SI homeland ownership used by /mobs and player travel.
    private static bool RegionIsAccessible(eRealm realm, ushort regionId)
    {
        if (regionId is 1 or 10 or 20 or 21 or 22 or 23 or 24 or 50 or 51 or 60 or 61 or 62)
            return realm == eRealm.Albion;
        if (regionId is 100 or 101 or 125 or 126 or 127 or 128 or 129 or 150 or 151 or 160 or 161)
            return realm == eRealm.Midgard;
        if (regionId is 180 or 181 or 190 or 191 or 192 or 193 or 194 or 200 or 201 or 220 or 221 or 222 or 223 or 224)
            return realm == eRealm.Hibernia;
        return true;
    }
}
