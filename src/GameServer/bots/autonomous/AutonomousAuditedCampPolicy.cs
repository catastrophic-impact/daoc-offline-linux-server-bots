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
        };

        public static bool IsBotExcludedSpawn(string internalId) =>
            !string.IsNullOrEmpty(internalId) && BotExcludedSpawnIds.Contains(internalId);
    }
}
