using System;
using System.Collections.Generic;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>AI-only shroom safety. Native player casts, pet stats and rewards are unchanged.</summary>
    public static class AnimistSingleTargetPolicy
    {
        private const string OwnerSpellDue = "autonomous.animist.owner-spell-due";

        public static bool AppliesTo(GameLiving owner) => owner is GameBot && !BotAnimistPolicy.AppliesTo(owner) ||
            owner is GamePlayer player && AutonomousPlayerPilot.IsActive(player);

        public static bool AllowsAutomatedSpell(GameLiving owner, Spell spell) =>
            BotAnimistPolicy.AppliesTo(owner) ? spell != null : AllowsAutomatedSpell(spell);

        public static bool IsTurretSummon(Spell spell) => spell != null &&
            (spell.SpellType == eSpellType.SummonAnimistPet || AutonomousPetSupport.IsAnimistFieldTurret(spell.SpellType));

        public static bool AllowsAutomatedSpell(Spell spell)
        {
            if (spell == null || spell.SpellType == eSpellType.TurretPBAoE) return false;
            if (!IsTurretSummon(spell)) return true;
            return AllowsSummon(spell, SkillBase.GetSpellByID,
                id => NpcTemplateMgr.GetTemplate(id)?.Spells);
        }

        public static bool AllowsSummon(Spell summon, Func<int, Spell> resolve,
            Func<int, IEnumerable<Spell>> templateSpells)
        {
            if (summon == null || !IsTurretSummon(summon)) return false;
            int budget = 32;
            bool hasPayload = false;
            IEnumerable<Spell> spells = templateSpells(summon.LifeDrainReturn);
            if (spells != null)
                foreach (Spell spell in spells)
                {
                    hasPayload = true;
                    if (!SingleTargetPayload(spell, resolve, ref budget)) return false;
                }
            if (summon.SubSpellID > 0)
            {
                hasPayload = true;
                if (!SingleTargetPayload(resolve(summon.SubSpellID), resolve, ref budget)) return false;
            }
            foreach (int id in summon.MultipleSubSpells)
            {
                hasPayload = true;
                if (!SingleTargetPayload(resolve(id), resolve, ref budget)) return false;
            }
            // Unknown payloads are not assumed safe; no free/made-up fallback summon.
            return hasPayload;
        }

        public static bool IsSingleTargetPayload(Spell spell)
        {
            int budget = 32;
            return SingleTargetPayload(spell, SkillBase.GetSpellByID, ref budget);
        }

        private static bool SingleTargetPayload(Spell spell, Func<int, Spell> resolve, ref int budget)
        {
            if (spell == null || --budget < 0 || spell.IsAoE ||
                spell.Target != eSpellTarget.ENEMY || !spell.IsHarmful)
                return false;
            // A damage component does not make a debuff turret acceptable:
            // slow+damage, resist-debuff nukes, taunts and all support shrooms
            // are deliberately excluded. Check every nested/template payload.
            bool directDamage = spell.Damage > 0 && spell.SpellType is
                eSpellType.DirectDamage or eSpellType.Bolt or eSpellType.Lifedrain;
            bool delivery = spell.SpellType == eSpellType.Bomber && spell.SubSpellID > 0;
            if (!directDamage && !delivery) return false;
            if (spell.SubSpellID > 0 && !SingleTargetPayload(resolve(spell.SubSpellID), resolve, ref budget)) return false;
            foreach (int id in spell.MultipleSubSpells)
                if (!SingleTargetPayload(resolve(id), resolve, ref budget)) return false;
            return true;
        }

        public static GameLiving AssistTarget(GameLiving owner)
        {
            if (owner?.IsAlive != true || owner.ObjectState != GameObject.eObjectState.Active ||
                (owner is GameBot bot ? bot.IsRecoveryResting : owner.IsSitting))
                return null;
            BotBrain brain = (owner as GameBot)?.Brain as BotBrain;
            GameLiving ordered = brain?.ActiveOrderedPullTarget;
            bool fighting = ordered != null || brain?.HasAggro == true || owner.InCombat || owner.IsAttacking || owner.IsCasting;
            GameLiving target = ordered ?? owner.TargetObject as GameLiving;
            return fighting && target?.IsAlive == true && target.ObjectState == GameObject.eObjectState.Active &&
                   target.CurrentRegion == owner.CurrentRegion && GameServer.ServerRules.IsAllowedToAttack(owner, target, true)
                ? target : null;
        }

        public static bool ShouldYieldToOwner(long until, long now, bool hasCastableOffense) =>
            hasCastableOffense && until > now;

        public static bool OwnerSpellPending(GameLiving owner, bool hasCastableOffense) =>
            ShouldYieldToOwner(owner.TempProperties.GetProperty<long>(OwnerSpellDue, 0), GameLoop.GameLoopTime, hasCastableOffense);

        public static void PlantedTurret(GameLiving owner, Spell spell) =>
            owner.TempProperties.SetProperty(OwnerSpellDue, GameLoop.GameLoopTime + spell.CastTime + 20_000);

        public static void CastOwnerSpell(GameLiving owner, Spell spell)
        {
            if (spell?.IsHarmful == true && !IsTurretSummon(spell) && spell.SpellType != eSpellType.TurretPBAoE)
                owner.TempProperties.RemoveProperty(OwnerSpellDue);
        }
    }
}
