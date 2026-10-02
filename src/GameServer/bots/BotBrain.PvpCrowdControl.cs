using System;
using System.Linq;
using DOL.GS;

namespace DOL.AI.Brain
{
    public partial class BotBrain
    {
        private long _nextPvpControl;
        private int _pvpControlSpellId;
        private long _pvpControlCastUntil;
        private bool PvpControlInFlight => Body.IsCasting && GameLoop.GameLoopTime<_pvpControlCastUntil &&
            Body.castingComponent?.SpellHandler?.Spell?.ID==_pvpControlSpellId;
        private bool TryPvpCrowdControl()
        {
            if (BotBody?.CanCastCrowdControlSpells != true || Body.IsCasting || Body.IsIncapacitated ||
                Body.castingComponent?.HasPendingSkillRequests == true || GameLoop.GameLoopTime < _nextPvpControl) return false;
            _nextPvpControl = GameLoop.GameLoopTime + 2000;
            if (!AggroList.Keys.Any(BotPvpCrowdControl.PlayerLike) && !BotPvpCrowdControl.PlayerLike(Body.TargetObject as GameLiving) &&
                Body.Group?.GetMembersInTheGroup().Any(m=>m.IsAttacking && BotPvpCrowdControl.PlayerLike(m.TargetObject as GameLiving))!=true) return false;
            // PvE never enters this policy; native levels, specs, mana, immunity,
            // interruption and spell timers still decide whether a cast succeeds.
            GameLiving[] enemies = Body.GetNPCsInRadius(1800).Where(BotPvpCrowdControl.PlayerLike).Cast<GameLiving>()
                .Concat(Body.GetPlayersInRadius(1800)).Where(t => BotSiegeRuntime.LegalEnemy(Body,t) && !t.IsMezzed &&
                    !t.IsStealthed && !BotPvpCrowdControl.Protected(Body,t) && CompanionEngagementMode.Allows(Body,t) &&
                    !CompanionPvpEngagement.Focused(Body,t) && !CompanionPvpEngagement.Defending(Body,t))
                .OrderBy(Body.GetDistanceTo).Take(24).ToArray();
            if (enemies.Length == 0) return false;
            foreach (Spell spell in BotBody.CrowdControlSpells.OrderByDescending(s=>s.SpellType==eSpellType.Mesmerize).ThenByDescending(s=>s.Radius).ThenByDescending(s=>s.Level))
            {
                if (spell.Level>Body.Level || Body.GetSkillDisabledDuration(spell)>0 || Body.Mana<BotBody.PowerCost(spell) ||
                    spell.CastTime>0 && Body.IsBeingInterrupted && !spell.Uninterruptible) continue;
                int range = spell.Target==eSpellTarget.SELF || spell.Range<=0 ? spell.Radius : spell.CalculateEffectiveRange(Body);
                GameLiving candidate=enemies.Where(t=>Body.IsWithinRadius(t,range) && !LivingHasEffect(t,spell) &&
                    NeedsOffensiveSpellApplication(t,spell)).OrderByDescending(t=>enemies.Count(n=>n.IsWithinRadius(t,Math.Max(1,spell.Radius))))
                    .ThenBy(Body.GetDistanceTo).FirstOrDefault(t=>BotSiegeRuntime.Visible(Body,t));
                if (candidate==null || !BotPvpCrowdControl.Reserve(BotBody,candidate,spell.CastTime+2000)) continue;
                GameObject previous=Body.TargetObject;
                Body.TargetObject=candidate;
                if (spell.CastTime>0) { Body.StopMovingOnPath(); Body.StopMoving(); }
                bool started=CheckOffensiveSpells(spell);
                if (!started) { BotPvpCrowdControl.Release(BotBody,candidate); Body.TargetObject=previous; continue; }
                _pvpControlSpellId=spell.ID; _pvpControlCastUntil=GameLoop.GameLoopTime+spell.CastTime+2000;
                if (spell.SpellType==eSpellType.Mesmerize && spell.Radius>0)
                    foreach(GameLiving enemy in enemies.Where(t=>t.IsWithinRadius(spell.Target==eSpellTarget.SELF ? Body : candidate,spell.Radius)))
                        BotPvpCrowdControl.Reserve(BotBody,enemy,spell.CastTime+2000);
                return true;
            }
            return false;
        }
    }
}
