using System;
using System.Collections.Generic;
using System.Linq;
using DOL.GS;

namespace DOL.AI.Brain
{
    public partial class BotBrain
    {
        private const long BardPveMezRetryMilliseconds = 15_000;
        private readonly Dictionary<GameLiving, long> _bardPveMezRetryUntil = new();

        private bool TryBardPveAddMez()
        {
            GameBot bard = BotBody;
            Group group = bard?.Group;
            if (bard?.CharacterClass?.ID != (int)eCharacterClass.Bard ||
                !BardBotCrowdControlPolicy.HasGroupForPveAdd(group?.MemberCount) ||
                bard.CanCastCrowdControlSpells != true ||
                bard.IsIncapacitated || bard.IsCasting ||
                bard.castingComponent?.HasPendingSkillRequests == true)
                return false;

            GameLiving[] members = group.GetMembersInTheGroup()
                .Where(member => member?.IsAlive == true && member.CurrentRegion == bard.CurrentRegion)
                .ToArray();
            if (members.Length < 2)
                return false;

            // The normal selector already owns the primary kill target. An add
            // must be attacking this exact group while somebody is fighting a
            // distinct monster; merely seeing a neutral mob cannot start a pull.
            HashSet<GameLiving> selectedTargets = new();
            HashSet<GameLiving> activeTargets = new();
            foreach (GameLiving member in members)
            {
                if (member.TargetObject is GameLiving target && target.IsAlive)
                {
                    selectedTargets.Add(target);
                    if ((member.IsAttacking || member.InCombat) && target is GameNPC)
                        activeTargets.Add(target);
                }

                if (member.ControlledBrain?.Body?.TargetObject is GameLiving petTarget && petTarget.IsAlive)
                {
                    selectedTargets.Add(petTarget);
                    if (petTarget is GameNPC)
                        activeTargets.Add(petTarget);
                }
            }
            if (activeTargets.Count == 0)
                return false;

            long now = GameLoop.GameLoopTime;
            foreach (GameLiving expired in _bardPveMezRetryUntil
                         .Where(pair => now >= pair.Value || pair.Key?.IsAlive != true)
                         .Select(pair => pair.Key).ToArray())
                _bardPveMezRetryUntil.Remove(expired);

            Spell[] singleTargetMezzes = bard.CrowdControlSpells
                .Where(spell => spell != null && spell.SpellType == eSpellType.Mesmerize &&
                    spell.Target == eSpellTarget.ENEMY && spell.Radius <= 0 && spell.Level <= bard.Level)
                .OrderByDescending(spell => spell.Level).ToArray();
            if (singleTargetMezzes.Length == 0)
                return false;

            foreach (GameNPC add in bard.GetNPCsInRadius(1800)
                         .Where(npc => npc?.IsAlive == true && npc.Realm == eRealm.None &&
                             !BotPvpCrowdControl.PlayerLike(npc) && CanDefendAgainst(npc))
                         .OrderBy(bard.GetDistanceTo).Take(24))
            {
                bool attacksMember = add.TargetObject is GameLiving victim && members.Contains(victim);
                bool alreadyControlled = add.IsMezzed || add.IsStunned ||
                    add.effectListComponent.ContainsEffectForEffectType(eEffect.MezImmunity) ||
                    add.effectListComponent.ContainsEffectForEffectType(eEffect.NPCMezImmunity);
                long retryUntil = _bardPveMezRetryUntil.GetValueOrDefault(add);
                if (!BardBotCrowdControlPolicy.IsSafePveAdd(true,
                        activeTargets.Any(target => target != add), attacksMember,
                        selectedTargets.Contains(add), Body.TargetObject == add,
                        add.HealthPercent, alreadyControlled, now, retryUntil))
                    continue;

                foreach (Spell spell in singleTargetMezzes)
                {
                    if (bard.Mana < bard.PowerCost(spell) || bard.GetSkillDisabledDuration(spell) > 0 ||
                        spell.CastTime > 0 && bard.IsBeingInterruptedByOther ||
                        !bard.IsWithinRadius(add, spell.CalculateEffectiveRange(bard)) ||
                        !NeedsOffensiveSpellApplication(add, spell) ||
                        !BotGroupSupport.HasCorpseLineOfSight(bard, add))
                        continue;

                    // The native casting request captures this target when it
                    // is queued. Restore the Bard's kill target immediately so
                    // the next combat decision resumes normal engagement.
                    if (_bardPveMezRetryUntil.Count >= 32)
                    {
                        GameLiving oldest = _bardPveMezRetryUntil
                            .OrderBy(pair => pair.Value).First().Key;
                        _bardPveMezRetryUntil.Remove(oldest);
                    }
                    _bardPveMezRetryUntil[add] = now + BardPveMezRetryMilliseconds;
                    GameObject previous = bard.TargetObject;
                    bard.TargetObject = add;
                    try
                    {
                        if (spell.CastTime > 0)
                        {
                            bard.StopMovingOnPath();
                            bard.StopMoving();
                        }
                        return CheckOffensiveSpells(spell);
                    }
                    finally
                    {
                        bard.TargetObject = previous;
                    }
                }
            }

            return false;
        }
    }
}
