using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>
    /// Which member fights hold an autonomous group in "Defending traveling group". Any member's
    /// fight anywhere used to freeze the whole group, while members only assist within 2,000
    /// units. Seen in Iarnwood on 2026-10-05: a Warrior fought a level 54 paralyzer alone
    /// somewhere else and the other six stood still indefinitely. Now:
    /// - only a member within 6,000 units of the leader (same region) holds the group, and the
    ///   others walk to help a member fighting beyond assist range;
    /// - aggro or an auto-attack with no real combat (no blow, hit or crowd control) for 60 s
    ///   no longer counts (an unreachable monster cannot hold a group forever).
    /// A fighting member farther away is left to its own fight; the trailing-member release
    /// (AutonomousGroupMemberStall) stops it holding the group's travel.
    /// </summary>
    public static class AutonomousGroupCombat
    {
        public const int HelpRadius = 6_000;
        public const long StaleEngagementMilliseconds = 60_000;

        private sealed class Engagement { public long Since; }
        private static readonly ConditionalWeakTable<GameBot, Engagement> Engagements = new();

        /// <summary>Pure rule: aggro/auto-attack without real combat counts only for 60 s.</summary>
        public static bool CountsAsFighting(bool inRealCombat, bool engaged, long engagedSince, long now) =>
            inRealCombat || engaged && (engagedSince == 0 || now - engagedSince < StaleEngagementMilliseconds);

        /// <summary>True while this member is in a fight that should matter to its group.</summary>
        public static bool IsFighting(GameBot member, long now)
        {
            if (member == null || !member.IsAlive)
                return false;
            bool real = member.InCombat;
            bool engaged = member.IsAttacking || (member.Brain as BotBrain)?.HasAggro == true;
            Engagement engagement = Engagements.GetOrCreateValue(member);
            lock (engagement)
            {
                if (real || !engaged)
                    engagement.Since = 0;
                else if (engagement.Since == 0)
                    engagement.Since = now;
                return CountsAsFighting(real, engaged, engagement.Since, now);
            }
        }

        /// <summary>A member's fight holds the group when it is near the leader.</summary>
        public static bool HoldsGroup(GameBot member, GameBot leader, long now) =>
            IsFighting(member, now) &&
            (leader == null || member == leader ||
             member.CurrentRegionID == leader.CurrentRegionID && member.IsWithinRadius(leader, HelpRadius));

        public static bool AnyHoldsGroup(IEnumerable<GameBot> members, GameBot leader, long now) =>
            members.Any(member => HoldsGroup(member, leader, now));

        /// <summary>
        /// The nearest fighting member beyond this bot's assist range but within help range,
        /// so a member standing idle in "Defending traveling group" walks over to help.
        /// </summary>
        public static GameBot MemberToHelp(GameBot bot, long now) =>
            bot?.Group?.GetMembersInTheGroup().OfType<GameBot>()
                .Where(member => member != bot && member.CurrentRegionID == bot.CurrentRegionID &&
                                 !bot.IsWithinRadius(member, BotBrain.GROUP_DEFENSE_ASSIST_RADIUS) &&
                                 bot.IsWithinRadius(member, HelpRadius) && IsFighting(member, now))
                .OrderBy(member => bot.GetDistanceTo(member))
                .FirstOrDefault();
    }
}
