using System;
using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>
    /// Extends temporary-companion triage to the exact player-led party's pet
    /// trees. Persistent player bots and pets belonging outside that party
    /// never enter this policy.
    /// </summary>
    public static class TemporaryCompanionPetHealing
    {
        private const int MaxOwnershipDepth = 16;

        public static bool TryGetSupportedPlayer(GameBot companion, out GamePlayer player)
        {
            player = companion?.Owner;
            Group group = companion?.Group;
            return companion?.IsTemporaryGroupHelper == true &&
                   player != null && companion.PlayerGroupLeader == player &&
                   group != null && player.Group == group &&
                   group.IsInTheGroup(companion) && group.IsInTheGroup(player);
        }

        public static bool IsDirectPlayerPet(GameLiving candidate, GamePlayer player)
        {
            if (candidate is GameBot || candidate is not GameNPC { Brain: IControlledBrain brain } || player == null)
                return false;

            var visited = new HashSet<GameLiving>(ReferenceEqualityComparer.Instance);
            GameLiving owner = brain.Owner;
            for (int depth = 0; owner != null && depth < MaxOwnershipDepth; depth++)
            {
                if (ReferenceEquals(owner, player))
                    return true;
                // ControlledMobBrain.GetPlayerOwner intentionally walks through
                // temporary GameBots to their creator. That is useful elsewhere,
                // but those are companion-owned pets and are excluded here.
                if (owner is GameBot || !visited.Add(owner))
                    return false;
                if (owner is not GameNPC { Brain: IControlledBrain parentBrain })
                    return false;
                owner = parentBrain.Owner;
            }

            return false;
        }

        public static bool IsPartyControlledPet(GameBot healer, GameLiving candidate, out bool playerOwned)
        {
            playerOwned = false;
            if (candidate is GameBot || candidate is not GameNPC { Brain: IControlledBrain brain } ||
                !TryGetSupportedPlayer(healer, out GamePlayer player))
                return false;

            var visited = new HashSet<GameLiving>(ReferenceEqualityComparer.Instance);
            GameLiving owner = brain.Owner;
            for (int depth = 0; owner != null && depth < MaxOwnershipDepth; depth++)
            {
                if (ReferenceEquals(owner, player))
                {
                    playerOwned = true;
                    return true;
                }

                if (owner is GameBot botOwner)
                {
                    Group group = healer.Group;
                    return botOwner.IsTemporaryGroupHelper && botOwner.PlayerGroupLeader == player &&
                           botOwner.Owner == player && botOwner.Group == group && group.IsInTheGroup(botOwner);
                }

                if (!visited.Add(owner) || owner is not GameNPC { Brain: IControlledBrain parentBrain })
                    return false;
                owner = parentBrain.Owner;
            }

            return false;
        }

        /// <summary>Lower values are healed first; health percentage breaks ties.</summary>
        public static int HealingPriority(GameBot healer, GameLiving target)
        {
            if (!TryGetSupportedPlayer(healer, out GamePlayer player) || target == null)
                return int.MaxValue;
            if (ReferenceEquals(target, player))
                return 0;
            if (IsPartyControlledPet(healer, target, out bool playerOwned))
                return playerOwned ? 1 : 3;
            if (target is GameBot companion && companion.Group == healer.Group &&
                healer.Group.IsInTheGroup(companion))
                return 2;
            return int.MaxValue;
        }

        public static bool PreferHealingTarget(GameBot healer, GameLiving candidate, GameLiving current)
        {
            if (candidate == null)
                return false;
            if (current == null)
                return true;
            int candidatePriority = HealingPriority(healer, candidate);
            int currentPriority = HealingPriority(healer, current);
            return candidatePriority < currentPriority ||
                   candidatePriority == currentPriority && candidate.HealthPercent < current.HealthPercent;
        }

        public static IReadOnlyList<GameNPC> TriageTargets(GameBot companion, int fieldPetSearchRadius)
        {
            if (!TryGetSupportedPlayer(companion, out GamePlayer player))
                return Array.Empty<GameNPC>();

            IEnumerable<GameNPC> nearbyPets = Array.Empty<GameNPC>();
            if (fieldPetSearchRadius > 0)
            {
                ushort radius = (ushort)Math.Clamp(fieldPetSearchRadius, 1, ushort.MaxValue);
                nearbyPets = companion.GetNPCsInRadius(radius);
            }

            HashSet<GameNPC> targets = CollectOwnedPets(player, player.ControlledBrain, nearbyPets);
            foreach (GameBot groupCompanion in companion.Group.GetMembersInTheGroup().OfType<GameBot>())
            {
                if (groupCompanion.IsTemporaryGroupHelper && groupCompanion.PlayerGroupLeader == player &&
                    groupCompanion.Owner == player)
                    AddAttachedTree(groupCompanion.ControlledBrain, targets,
                        new HashSet<IControlledBrain>(ReferenceEqualityComparer.Instance), 0);
            }
            foreach (GameNPC nearby in nearbyPets)
            {
                if (IsPartyControlledPet(companion, nearby, out _))
                    targets.Add(nearby);
            }
            targets.RemoveWhere(target => !IsLegalHealingCandidate(companion, target));
            return new List<GameNPC>(targets)
                .OrderBy(target => HealingPriority(companion, target))
                .ThenBy(target => target.HealthPercent)
                .ToList();
        }

        public static HashSet<GameNPC> CollectOwnedPets(GamePlayer player, IControlledBrain attachedRoot,
            IEnumerable<GameNPC> nearbyPets)
        {
            var targets = new HashSet<GameNPC>(ReferenceEqualityComparer.Instance);
            AddAttachedTree(attachedRoot, targets,
                new HashSet<IControlledBrain>(ReferenceEqualityComparer.Instance), 0);
            if (nearbyPets == null)
                return targets;

            foreach (GameNPC nearby in nearbyPets)
            {
                if (IsDirectPlayerPet(nearby, player))
                    targets.Add(nearby);
            }
            return targets;
        }

        public static void AdjustHealingTargets(GameLiving caster, Spell spell, List<GameLiving> targets, bool addPlayerPets)
        {
            if (spell?.IsHealing != true || targets == null ||
                caster is not GameBot companion || !TryGetSupportedPlayer(companion, out GamePlayer player))
                return;

            // Native GROUP/CombatHeal expansion can include unrelated pets.
            // Retain the exact player-led party's pet trees and ordinary group
            // members, while rejecting pets owned outside this party.
            targets.RemoveAll(target => target is GameNPC { Brain: IControlledBrain } and not GameBot &&
                                               !IsPartyControlledPet(companion, target, out _));

            if (!addPlayerPets)
                return;

            int range = spell.Range == 0 ? spell.Radius : spell.CalculateEffectiveRange(caster);
            if (range <= 0)
                return;

            foreach (GameNPC pet in TriageTargets(companion, range))
            {
                if (caster.IsWithinRadius(pet, range) && !targets.Contains(pet))
                    targets.Add(pet);
            }
        }

        private static bool IsLegalHealingCandidate(GameBot companion, GameNPC target) =>
            target != null && target.IsAlive && target.ObjectState == GameObject.eObjectState.Active &&
            target.CurrentRegionID == companion.CurrentRegionID && IsPartyControlledPet(companion, target, out _) &&
            GameServer.ServerRules.IsSameRealm(companion, target, true) &&
            !GameServer.ServerRules.IsAllowedToAttack(companion, target, true);

        private static void AddAttachedTree(IControlledBrain brain, HashSet<GameNPC> targets,
            HashSet<IControlledBrain> visited, int depth)
        {
            if (brain?.Body == null || depth >= MaxOwnershipDepth || !visited.Add(brain))
                return;

            GameNPC body = brain.Body;
            targets.Add(body);
            if (body.ControlledNpcList == null)
                return;

            foreach (IControlledBrain child in body.ControlledNpcList)
                AddAttachedTree(child, targets, visited, depth + 1);
        }
    }
}
