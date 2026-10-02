using System.Linq;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>Ephemeral /spawn helpers only. Never changes persistent character recovery.</summary>
    public static class TemporaryCompanionRecovery
    {
        public const int MaximumLeaderDistance = 3000;
        public const int CheckIntervalMilliseconds = 2000;

        public static bool CanReleaseToOwner(bool temporary, bool active, bool sameGroup,
            bool ownerAlive, bool ownerActive, bool partyInCombat) =>
            temporary && active && sameGroup && ownerAlive && ownerActive && !partyInCombat;

        public static bool CanReleaseToOwner(GameBot companion)
        {
            GamePlayer owner = companion?.Owner;
            bool sameGroup = owner != null && companion.Group != null && companion.Group == owner.Group &&
                companion.Group.IsInTheGroup(companion) && companion.Group.IsInTheGroup(owner);
            return companion != null && CanReleaseToOwner(companion.IsTemporaryGroupHelper,
                companion.ObjectState == GameObject.eObjectState.Active, sameGroup,
                owner?.IsAlive == true, owner?.ObjectState == GameObject.eObjectState.Active,
                HasPartyCombat(companion) || HasCombatNearOwner(owner));
        }

        private static bool HasCombatNearOwner(GamePlayer owner) => owner?.Group != null &&
            owner.Group.GetMembersInTheGroup().Any(member => member.IsAlive &&
                member.CurrentRegionID == owner.CurrentRegionID && owner.IsWithinRadius(member, MaximumLeaderDistance) &&
                (member.InCombat || member.IsAttacking ||
                 member.ControlledBrain?.Body is { IsAlive: true, InCombat: true } ||
                 member is GameBot { Brain: BotBrain { HasAggro: true } }));

        public static bool ShouldRecall(bool temporary, bool alive, bool active, bool sameGroup,
            bool followsOwner, bool riding, bool ownerRiding, bool sameRegion, int distance, bool released) =>
            temporary && alive && active && sameGroup && followsOwner && !riding &&
            (released || !ownerRiding && (!sameRegion || distance > MaximumLeaderDistance));

        public static bool HasPartyCombat(GameBot companion)
        {
            if (companion?.Group == null) return false;
            return companion.Group.GetMembersInTheGroup().Any(member =>
                member.IsAlive && member.CurrentRegionID == companion.CurrentRegionID &&
                companion.IsWithinRadius(member, MaximumLeaderDistance) &&
                (member.InCombat || member.IsAttacking ||
                 member.ControlledBrain?.Body is { IsAlive: true, InCombat: true } ||
                 member is GameBot { Brain: BotBrain { HasAggro: true } }));
        }

        public static bool CanPrioritizeOwnerResurrection(bool temporary, bool companionAlive, bool ownerAlive,
            bool sameGroup, bool followsOwner, bool hasLearnedResurrection, bool partyInCombat) =>
            temporary && companionAlive && !ownerAlive && sameGroup && followsOwner &&
            hasLearnedResurrection && !partyInCombat;
    }
}
