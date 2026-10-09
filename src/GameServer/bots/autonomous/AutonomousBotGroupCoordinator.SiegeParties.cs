using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS;

public static partial class AutonomousBotGroupCoordinator
{
    // Siege armies move as full groups (owner 2026-10-07): the bots that answered a siege, a defense or a relic battle on
    // their own are merged into parties of eight with the nearby recruits of the same event and side. Smaller parties
    // (three or more) form only once the army is marching or fighting, so nobody is left walking alone. The party takes
    // over its members' reservations (same cap) and keeps the siege; a party forms where its members already stand, so
    // it assembles at once instead of walking to a far rendezvous.
    private const int SiegePartySize = 8;
    private const int SiegePartyMinimumWhileMarching = 3;
    private const int SiegePartyGatherRadius = 2_500;
    private static long _nextSiegePartyTick;

    private static void FormSiegeParties()
    {
        long now = GameLoop.GameLoopTime;
        if (now < _nextSiegePartyTick) return;
        _nextSiegePartyTick = now + 10_000;

        var byId = AutonomousBotRegistry.Snapshot().ToDictionary(bot => (long)bot.DatabaseID);
        int formed = 0;
        foreach (AutonomousRvrEventLayer.SoloSiegeRoster roster in AutonomousRvrEventLayer.SoloSiegeRosters(now))
        {
            var free = roster.MemberIds
                .Select(id => byId.GetValueOrDefault(id))
                .Where(bot => bot != null && bot.IsAlive && bot.Group == null && bot.Realm == roster.Realm &&
                              bot.CurrentRegion != null && !bot.InCombat && !bot.IsTemporaryGroupHelper &&
                              !bot.IsPlayerLedGroup && !bot.IsOnStableMasterRoute && !AutonomousRealmRaid.IsReserved(bot) &&
                              !GameRelic.IsPlayerCarryingRelic(bot))
                .ToList();
            int minimum = roster.Marching ? SiegePartyMinimumWhileMarching : SiegePartySize;
            while (free.Count >= minimum && formed < 12)
            {
                GameBot leader = free[0];
                GameBot[] mates = free.Skip(1)
                    .Where(bot => bot.CurrentRegionID == leader.CurrentRegionID && bot.IsWithinRadius(leader, SiegePartyGatherRadius))
                    .OrderBy(bot => bot.GetDistanceTo(leader))
                    .Take(SiegePartySize - 1)
                    .ToArray();
                free.Remove(leader);
                if (mates.Length + 1 < minimum) continue;
                GameBot[] members = new[] { leader }.Concat(mates).ToArray();
                if (TryFormSiegeParty(roster, members)) formed++;
                foreach (GameBot mate in mates) free.Remove(mate);
            }
        }
    }

    private static bool TryFormSiegeParty(AutonomousRvrEventLayer.SoloSiegeRoster roster, GameBot[] members)
    {
        GameBot leader = members[0];
        lock (Sync)
        {
            var group = new Group(leader);
            GroupMgr.AddGroup(group);
            if (!group.AddMember(leader))
            {
                GroupMgr.RemoveGroup(group);
                return false;
            }
            foreach (GameBot member in members.Skip(1)) group.AddMember(member);
            GameBot[] joined = BotMembers(group);
            if (joined.Length < 2)
            {
                group.DisbandGroup();
                return false;
            }
            // Assemble where the leader stands (members are within a few thousand units), not at a far border keep.
            var here = new SharedCamp($"siege-party:{roster.EventId}", "siege party", leader.CurrentZone?.Description ?? "the field",
                leader.CurrentRegionID, leader.X, leader.Y, leader.Z, false, true);
            Session session = NewSession(group, leader, joined.Length, eAutonomousObjectiveKind.RvR, here);
            if (session == null ||
                !AutonomousRvrEventLayer.MergeSoloForces(roster.EventId, roster.Realm, joined.Select(b => (long)b.DatabaseID).ToArray(), session.Id))
            {
                group.DisbandGroup();
                return false;
            }
            Sessions[group] = session;
            foreach (GameBot member in joined)
                member.TempProperties.SetProperty("RvrEventForce", session.Id);
            WriteSessionMetadata(session, joined);
            Log.Info($"RVR_SIEGE_PARTY_FORMED event={roster.EventId} group={session.Id} realm={GlobalConstants.RealmToName(roster.Realm)} " +
                     $"size={joined.Length} marching={roster.Marching} members=\"{string.Join(",", joined.Select(b => b.Name))}\"");
            return true;
        }
    }
}
