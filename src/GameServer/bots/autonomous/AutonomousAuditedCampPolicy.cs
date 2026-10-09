using System;
using System.Collections.Generic;

namespace DOL.GS
{
    /// <summary>Measured historical-anchor/live-level mismatches, not new spawns.</summary>
    public static class AutonomousAuditedCampPolicy
    {
        // The Salisbury spirit spawn is surrounded within a few hundred units
        // by aggressive undead druids and filidh above the spirit's level.
        // Keep the real target available to formed parties, but do not send a
        // solo leveler into the pack for a nominally green spirit goal.
        public static bool CanAssignToParty(string campId, int partySize) =>
            !string.Equals(campId, "capnbry:1:1:3:spirit", StringComparison.OrdinalIgnoreCase) ||
            partySize >= 4;

        public static bool UsesLiveAnchor(ushort region, string name) =>
            (region == 200 && name?.ToLowerInvariant() is
                "orchard nipper" or "lugradan whelp" or "luricaduane" or "hill toad" or "feccan") ||
            (region == 51 && name?.Equals("large dragonfly", StringComparison.OrdinalIgnoreCase) == true) ||
            (region == 151 && name?.Equals("boobrie hatchling", StringComparison.OrdinalIgnoreCase) == true) ||
            (region == 100 && name?.ToLowerInvariant() is "huldu outcast" or "green serpent");

        /// <summary>
        /// These outdoor spawns contain cliffs, raised props, or isolated mesh
        /// islands close enough to an otherwise valid camp anchor to pass the
        /// ordinary radius lookup. Prove a real, reversible melee approach
        /// before an autonomous bot commits to one of those individual mobs.
        /// </summary>
        public static bool RequiresVerifiedTargetRoute(ushort region, string name) =>
            UsesLiveAnchor(region, name) && name?.ToLowerInvariant() is
                "large dragonfly" or "boobrie hatchling" or "feccan" or "huldu outcast" or "green serpent";

        // Individual low-level spawns whose nearest aggressive neighbour is 15+
        // levels higher and can roam into aggro range of the pull (audited
        // 2026-09-30). The creatures stay in the world for players; bots never
        // build a camp on them or pull them. Every other spawn of the same
        // name remains a normal bot goal.
        private static readonly HashSet<string> BotExcludedSpawnIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "c0502019-4ad3-41e7-9cee-eb68fcaba396", // hungry shriller, Caillte Garran: pollen spore 28-35 at 1,037
            "1baf88ca-140e-4424-9d0d-bd66be4a2aeb", // bantam spectre, Cliffs of Moher: grovewood 38-40 at 71
            "7e88a70e-855e-4142-a667-4dd82cfcca53", // bocan, Cliffs of Moher: fog wraith 28-30 at 806
            "14ff0b3f-117d-4b0a-b56f-cc2d92900444", // fetch, Cliffs of Moher: fog wraith 28-30 at 951
            "4a660ab5-6944-49a7-8b48-be40904104b2", // fetch, Cliffs of Moher: cliff beetle 31-37 at 685
            "190339b0-ac23-42be-8b3a-7ec1f03273ed", // giant beetle, Cliffs of Moher: cliff beetle 31-37 at 332
            "a714840c-3c7f-40ac-823d-6daa722472ae", // giant beetle, Cliffs of Moher: grovewood 38-40 at 325
            "738dcc64-f1d5-4644-b8f5-b10c3608bc7b", // koalinth sentinel, Cliffs of Moher: cliff dweller 36-38 at 206
            "b181e3f8-c434-4dc3-aa12-45f02f4004fe", // koalinth sentinel, Cliffs of Moher: cliff dweller 36-38 at 567

            // Every wiggle worm in Bog of Cullen (all 13, audited 2026-10-01). Level 0
            // ambient worms in a zone whose monsters are mostly level 40+: they
            // con blue to level 1 bots, which then walked across the realm into
            // that zone for almost no XP and died on the way.
            "30a66daf-1655-4961-b140-bc8269481fd8",
            "9c8aa63b-abca-4f2b-99fa-dc974adc5e49",
            "27ed2dfc-39bf-49c6-ae49-8d3980a6a6d1",
            "926313a3-a713-439a-bee9-4868f2dc60d0",
            "27d209e2-503c-44db-a4ec-6ea72079d9a1",
            "89f8f5de-42c5-4b83-a8f7-be0d42f8f599",
            "86000309-c6e5-4204-90cc-de753e4a42fb",
            "35a3cb9e-8f54-462e-ba4f-122f1bffd539",
            "2205b343-8eab-415c-b3d4-217d6a7b2d93",
            "ffff829e-dfcd-4ef5-aa01-4936470426bd",
            "600f6a45-b6bc-4664-af6b-fab72799a668",
            "2559e2ef-7151-4cc7-a1bf-b89e1ed5e254",
            "fdad6d7b-3c93-475a-8fba-8adcb7d49440",
        };

        public static bool IsBotExcludedSpawn(string internalId) =>
            !string.IsNullOrEmpty(internalId) && BotExcludedSpawnIds.Contains(internalId);

        /// <summary>
        /// Spawns gamebots never hunt: the audited list above plus classic quest monsters (a player's quest event
        /// spawn or a named quest monster), so a bot never takes a player's quest kill.
        /// </summary>
        public static bool IsBotExcludedNpc(GameNPC npc) =>
            npc != null && (IsBotExcludedSpawn(npc.InternalID) || Quests.ClassicQuests.IsQuestMonster(npc));
    }
}
