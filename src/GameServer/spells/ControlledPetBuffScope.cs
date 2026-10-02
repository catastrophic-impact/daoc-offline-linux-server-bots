using System.Collections.Generic;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>
    /// Defines the ownership boundary for positive PET-target buffs. Temporary
    /// companion GameBots deliberately expose a controlled-brain owner so the
    /// human can command them, but they are player-character party members, not
    /// members of that human's summoned-pet tree.
    /// </summary>
    public static class ControlledPetBuffScope
    {
        private const int MaxOwnershipDepth = 16;

        public static bool IsOwnedPetTreeMember(GameLiving caster, GameNPC candidate)
        {
            if (caster == null || candidate == null || candidate is GameBot ||
                candidate.Brain is not IControlledBrain brain)
                return false;

            var visited = new HashSet<GameLiving>(ReferenceEqualityComparer.Instance)
            {
                candidate
            };
            GameLiving owner = brain.Owner;

            for (int depth = 0; owner != null && depth < MaxOwnershipDepth; depth++)
            {
                if (ReferenceEquals(owner, caster))
                    return true;

                // Crossing through a GameBot would turn that companion and its
                // own pets into the human's pet tree. Stop at that boundary.
                if (owner is GameBot || !visited.Add(owner) ||
                    owner is not GameNPC { Brain: IControlledBrain parentBrain })
                    return false;

                owner = parentBrain.Owner;
            }

            return false;
        }
    }
}
