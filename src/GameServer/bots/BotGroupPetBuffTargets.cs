using System.Collections.Generic;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>Attached party pets for shareable buffs, never an expansion of own-pet spells.</summary>
    public static class BotGroupPetBuffTargets
    {
        public static bool IsShareable(Spell spell) => spell != null && !spell.IsHarmful &&
            spell.Target is eSpellTarget.REALM or eSpellTarget.GROUP;

        public static IEnumerable<GameNPC> Enumerate(GameBot caster, Spell spell)
        {
            Group group = caster?.Group;
            if (group == null || !group.IsInTheGroup(caster) || !IsShareable(spell))
                yield break;

            int range = spell.Range == 0 ? spell.Radius : spell.CalculateEffectiveRange(caster);
            var visited = new HashSet<IControlledBrain>();
            IEnumerable<GameLiving> members = spell.Target == eSpellTarget.REALM
                ? AutonomousRealmRaid.SupportMembers(caster) : group.GetMembersInTheGroup();
            object supportScope = AutonomousRealmRaid.SupportScope(caster);
            foreach (GameLiving member in members)
            {
                // Event rosters are snapshots. A departed member must not leave
                // its pet eligible until the next roster rebuild. Cross-party
                // Realm buffs require the same still-active event, not merely
                // a reference left in a former group's member list.
                if (member?.Group == null || !member.Group.IsInTheGroup(member) ||
                    member.Group != group && (spell.Target != eSpellTarget.REALM ||
                        member is not GameBot ally ||
                        !ReferenceEquals(supportScope, AutonomousRealmRaid.SupportScope(ally))))
                    continue;
                // Match native GROUP expansion: the owner must be in range too.
                if (spell.Target == eSpellTarget.GROUP && !caster.IsWithinRadius(member, range))
                    continue;
                foreach (GameNPC pet in AttachedTree(member.ControlledBrain, member, visited,
                             spell.Target == eSpellTarget.GROUP ? 2 : 16))
                {
                    if (pet.IsAlive && pet.ObjectState == GameObject.eObjectState.Active &&
                        pet.CurrentRegionID == caster.CurrentRegionID && pet.Realm == caster.Realm &&
                        caster.IsWithinRadius(pet, range))
                        yield return pet;
                }
            }
        }

        // Bounded ownership walk, including Bonedancer sub-pets. No world scan or database work.
        public static IEnumerable<GameNPC> AttachedTree(IControlledBrain brain, GameLiving owner,
            HashSet<IControlledBrain> visited, int remainingDepth = 16)
        {
            if (remainingDepth <= 0 || brain?.Body == null || brain.Body is GameBot ||
                brain.Owner != owner || !visited.Add(brain))
                yield break;
            GameNPC pet = brain.Body;
            yield return pet;
            if (pet.ControlledNpcList == null)
                yield break;
            foreach (IControlledBrain child in pet.ControlledNpcList)
                foreach (GameNPC descendant in AttachedTree(child, pet, visited, remainingDepth - 1))
                    yield return descendant;
        }
    }
}
