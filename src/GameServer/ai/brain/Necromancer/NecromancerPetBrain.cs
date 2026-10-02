using System;
using System.Collections.Concurrent;
using DOL.Events;
using DOL.GS;
using DOL.GS.PacketHandler;
using DOL.GS.Spells;
using DOL.Language;

namespace DOL.AI.Brain
{
    /// <summary>
    /// A brain for the necromancer pets.
    /// </summary>
    /// <author>Aredhel</author>
    public class NecromancerPetBrain : ControlledMobBrain
    {
        public NecromancerPetBrain(GameLiving owner) : base(owner) { }

        public override int ThinkInterval => 500;

        public void CancelCompanionTravelBuffs()
        {
            if (Owner is not GameBot bot || !CompanionFollowPolicy.WaitingForLeaderToStop(bot) ||
                !HasPendingSelfBuffCommand) return;
            // The native helpful-command queue is at most two entries.
            var retained = new System.Collections.Generic.List<SpellQueueEntry>(2);
            int count = m_spellQueue.Count;
            for (int i = 0; i < count && m_spellQueue.TryDequeue(out SpellQueueEntry entry); i++)
                if (entry.Target != Body || !CompanionFollowPolicy.IsOrdinaryBuff(Body, entry.Spell))
                {
                    retained.Add(entry);
                    m_spellQueue.Enqueue(entry);
                }
            // Native StopCurrentSpellcast also clears this queue. Preserve
            // any non-buff command that was waiting behind the canceled buff.
            if (CompanionFollowPolicy.CancelOrdinaryBuffs(Body))
                foreach (SpellQueueEntry entry in retained) m_spellQueue.Enqueue(entry);
        }

        // Native casting requests are asynchronous. The queue can be empty
        // before IsCasting becomes true, including while waiting for LoS.
        public bool HasPendingBotCommand => HasSpellsQueued() || HasAttackSpellsQueued() ||
            Body?.IsCasting == true || Body?.castingComponent?.HasPendingSkillRequests == true ||
            Body?.castingComponent?.HasPendingLosCheckRequests == true;

        /// <summary>
        /// True only while the servant is verifiably processing a helpful
        /// command on itself. A previous enemy may remain in the aggro list
        /// until maintenance runs; that stale flag must not cancel this cast.
        /// </summary>
        public bool HasPendingSelfBuffCommand
        {
            get
            {
                if (Body == null)
                    return false;

                SpellHandler current = Body.castingComponent?.SpellHandler;
                if (current?.Spell != null)
                    return current.Target == Body && !current.Spell.IsHarmful;

                SpellHandler queued = Body.castingComponent?.QueuedSpellHandler;
                if (queued?.Spell != null)
                    return queued.Target == Body && !queued.Spell.IsHarmful;

                if (m_spellQueue.TryPeek(out SpellQueueEntry command))
                    return command.Target == Body && command.Spell?.IsHarmful == false;

                return Body.TargetObject == Body &&
                       Body.castingComponent?.TryPeekPendingSpell(out Spell pending) == true &&
                       pending?.IsHarmful == false;
            }
        }

        /// <summary>
        /// Brain main loop.
        /// </summary>
        public override void Think()
        {
            // The shade issues a servant self-buff while its own route may still
            // be running. Follow movement then restarts the three-second servant
            // cast on every pet pulse. Freeze only this verified helpful
            // self-command. Any attack against the shade or servant immediately
            // releases the hold and lets the normal combat FSM defend them.
            if (HasPendingSelfBuffCommand && Owner is GameBot { Brain: BotBrain ownerBrain } owner &&
                !owner.InCombat && !owner.IsAttacking && !ownerBrain.HasAggro &&
                !Body.InCombat && !Body.IsAttacking)
            {
                if (Body.IsMoving)
                    Body.StopMoving();
                CheckSpellQueue();
                return;
            }

            CheckTether();
            FSM.Think();
        }

        public override void Attack(GameObject target)
        {
            if (m_orderAttackTarget == null && !Body.InCombat)
                ClearAttackSpellQueue();

            base.Attack(target);
        }

        public override void Disengage()
        {
            base.Disengage();
            ClearAttackSpellQueue();
        }

        public override void OnAttackedByEnemy(AttackData ad)
        {
            base.OnAttackedByEnemy(ad);

            // The servant is the vulnerable half of a Necromancer. Persistent
            // and temporary GameBot shades have no human input to cancel an
            // autonomous route, so hand a real hit back to the owner at once.
            // The ordinary BotBrain then chooses the legal spell/range action;
            // this does not give the shade a separate combat implementation.
            if (Owner is not GameBot { Brain: BotBrain ownerBrain } owner ||
                ad?.GeneratesAggro != true || ad.Attacker is not GameLiving attacker ||
                !attacker.IsAlive || attacker.ObjectState is not GameObject.eObjectState.Active ||
                attacker.CurrentRegion != owner.CurrentRegion ||
                !GameServer.ServerRules.IsAllowedToAttack(owner, attacker, true))
            {
                return;
            }

            owner.WakeRecoveryRest();
            owner.StopMovingOnPath();
            owner.StopFollowing();
            owner.StopMoving();
            ownerBrain.AddToAggroList(attacker, Math.Max(1, ad.Damage + ad.CriticalDamage));
            BotBrain.EnterOwnedPetCombatState(ownerBrain.FSM);
            ownerBrain.NextThinkTick = GameLoop.GameLoopTime;
        }

        #region Events

        public void OnOwnerFinishPetSpellCast(Spell spell, SpellLine spellLine, GameLiving target)
        {
            if (Body.IsCasting)
                MessageToHumanOwner("AI.Brain.Necromancer.CastSpellAfterAction", eChatType.CT_System, Body.Name);

            if (spell.IsInstantCast)
                AddToAttackSpellQueue(spell, spellLine, target);
            else
                AddToSpellQueue(spell, spellLine, target);

            // Immediately try to cast.
            CheckSpellQueue();
        }

        public void OnPetBeginCast(Spell spell, SpellLine spellLine)
        {
            DebugMessageToOwner($"Now casting '{spell}'");

            // This message is for spells from the spell queue only, so suppress it for insta cast buffs coming from the pet itself.
            if (spellLine.Name != NecromancerPet.PetInstaSpellLine)
            {
                Owner.Notify(GameLivingEvent.CastStarting, Body, new CastingEventArgs(Body.CurrentSpellHandler));
                MessageToHumanOwner("AI.Brain.Necromancer.PetCastingSpell", eChatType.CT_System, Body.Name);
            }
        }

        /// <summary>
        /// Process events.
        /// </summary>
        public override void Notify(DOLEvent e, object sender, EventArgs args)
        {
            base.Notify(e, sender, args);

            if (e == GameLivingEvent.CastFailed)
            {
                // Tell owner why cast has failed.
                switch ((args as CastFailedEventArgs).Reason)
                {
                    case CastFailedEventArgs.Reasons.TargetTooFarAway:
                        MessageToHumanOwner("AI.Brain.Necromancer.ServantFarAwayToCast", eChatType.CT_SpellResisted);
                        break;

                    case CastFailedEventArgs.Reasons.TargetNotInView:
                        MessageToHumanOwner("AI.Brain.Necromancer.PetCantSeeTarget", eChatType.CT_SpellResisted, Body.Name);
                        break;

                    case CastFailedEventArgs.Reasons.NotEnoughPower:
                        RemoveSpellFromQueue();
                        MessageToHumanOwner("AI.Brain.Necromancer.NoPower", eChatType.CT_SpellResisted, Body.Name);
                        break;
                }
            }
            else if (e == GameLivingEvent.AttackFinished)
                Owner.Notify(GameLivingEvent.AttackFinished, Owner, args);
        }

        #endregion

        #region Spell Queue

        /// <summary>
        /// See if there are any spells queued up and if so, get the first one
        /// and cast it.
        /// </summary>
        public bool CheckSpellQueue()
        {
            // Only start casting if the pet has finished his attack round.
            // This will be false most of the time, unless called from the attack component directly.
            if (!GameServiceUtils.ShouldTick(Body.attackComponent.attackAction.NextTick))
            {
                MessageToHumanOwner("AI.Brain.Necromancer.CastSpellAfterAction", eChatType.CT_System, Body.Name);
                return false;
            }

            // Check the attack spell queue before casting spells.
            // This allows instant spells such as FP to be activated first.
            if (!Body.IsCasting)
                CheckAttackSpellQueue();

            SpellQueueEntry entry = GetSpellFromQueue();

            if (entry == null || !CastSpell(entry.Spell, entry.SpellLine, entry.Target, true))
                return false;

            RemoveSpellFromQueue();
            return true;
        }

        /// <summary>
        /// See if there are any spells queued up and if so, get the first one and cast it.
        /// </summary>
        public void CheckAttackSpellQueue()
        {
            int spellsQueued = m_attackSpellQueue.Count;

            for (int i = 0; i < spellsQueued; i++)
            {
                SpellQueueEntry entry = GetSpellFromAttackQueue();

                if (entry == null || !CastSpell(entry.Spell, entry.SpellLine, entry.Target, false))
                    continue;

                RemoveSpellFromAttackQueue();
            }
        }

        /// <summary>
        /// Try to cast a spell, returning true if the spell started to cast.
        /// </summary>
        /// <returns>Whether or not the spell started to cast.</returns>
        private bool CastSpell(Spell spell, SpellLine line, GameObject target, bool checkLos)
        {
            GameLiving spellTarget = target as GameLiving;

            // Target must be alive, or this is a self spell, or this is a pbaoe spell.
            if ((spellTarget != null && spellTarget.IsAlive) || spell.Target == eSpellTarget.SELF || spell.Range == 0)
            {
                if (spell.CastTime > 0)
                    Body.attackComponent.StopAttack();

                Body.TargetObject = spellTarget;
                return Body.CastSpell(spell, line, checkLos);
            }
            else
            {
                DebugMessageToOwner("Invalid target for spell '" + spell.Name + "'");
                return false;
            }
        }

        /// <summary>
        /// This class holds a single entry for the spell queue.
        /// </summary>
        private class SpellQueueEntry
        {
            public Spell Spell { get; }
            public SpellLine SpellLine { get; }
            public GameLiving Target { get; }

            public SpellQueueEntry(Spell spell, SpellLine spellLine, GameLiving target)
            {
                Spell = spell;
                SpellLine = spellLine;
                Target = target;
            }

            public SpellQueueEntry(SpellQueueEntry entry) : this(entry.Spell, entry.SpellLine, entry.Target) { }
        }

        private ConcurrentQueue<SpellQueueEntry> m_spellQueue = new();
        private ConcurrentQueue<SpellQueueEntry> m_attackSpellQueue = new();

        public void ClearSpellQueue()
        {
            m_spellQueue.Clear();
        }

        public void ClearAttackSpellQueue()
        {
            m_attackSpellQueue.Clear();
        }

        /// <summary>
        /// Whether or not any spells are queued.
        /// </summary>
        public bool HasSpellsQueued()
        {
            return !m_spellQueue.IsEmpty;
        }

        /// <summary>
        /// Whether or not any spells are queued.
        /// </summary>
        public bool HasAttackSpellsQueued()
        {
            return !m_attackSpellQueue.IsEmpty;
        }

        /// <summary>
        /// Fetches a spell from the queue without removing it; the spell is removed *after* the spell has finished casting.
        /// </summary>
        /// <returns>The next spell or null, if no spell is in the queue.</returns>
        private SpellQueueEntry GetSpellFromQueue()
        {
            m_spellQueue.TryPeek(out SpellQueueEntry spellQueueEntry);

            if (!m_spellQueue.IsEmpty)
                DebugMessageToOwner(string.Format("Grabbing spell '{0}' from the start of the queue in order to cast it", spellQueueEntry.Spell.Name));

            return spellQueueEntry;
        }

        /// <summary>
        /// Fetches a spell from the queue without removing it; the spell is removed *after* the spell has finished casting.
        /// </summary>
        /// <returns>The next spell or null, if no spell is in the queue.</returns>
        private SpellQueueEntry GetSpellFromAttackQueue()
        {
            m_attackSpellQueue.TryPeek(out SpellQueueEntry spellQueueEntry);

            if (spellQueueEntry != null)
                DebugMessageToOwner(string.Format("Grabbing spell '{0}' from the start of the queue in order to cast it", spellQueueEntry.Spell.Name));

            return spellQueueEntry;
        }

        /// <summary>
        /// Removes the spell that is first in the queue.
        /// </summary>
        public void RemoveSpellFromQueue()
        {
            m_spellQueue.TryDequeue(out SpellQueueEntry spellQueueEntry);

            if (spellQueueEntry != null)
                DebugMessageToOwner(string.Format("Removing spell '{0}' from the start of the queue", spellQueueEntry.Spell.Name));
        }

        /// <summary>
        /// Removes the spell that is first in the queue.
        /// </summary>
        public void RemoveSpellFromAttackQueue()
        {
            m_attackSpellQueue.TryDequeue(out SpellQueueEntry spellQueueEntry);

            if (spellQueueEntry != null)
                DebugMessageToOwner(string.Format("Removing spell '{0}' from the start of the queue", spellQueueEntry.Spell.Name));
        }

        /// <summary>
        /// Add a spell to the queue. If there are already 2 spells in the
        /// queue, remove the spell that the pet would cast next.
        /// </summary>
        /// <param name="spell">The spell to add.</param>
        /// <param name="spellLine">The spell line the spell is in.</param>
        /// <param name="target">The target to cast the spell on.</param>
        private void AddToSpellQueue(Spell spell, SpellLine spellLine, GameLiving target)
        {
            SpellQueueEntry spellQueueEntry = null;

            while (m_spellQueue.Count >= 2)
                m_spellQueue.TryDequeue(out spellQueueEntry);

            if (spellQueueEntry != null)
                MessageToHumanOwner("AI.Brain.Necromancer.SpellNoLongerInQueue", eChatType.CT_Spell, spellQueueEntry.Spell.Name, Body.Name);

            DebugMessageToOwner(string.Format("Adding spell '{0}' to the end of the queue", spell.Name));
            m_spellQueue.Enqueue(new SpellQueueEntry(spell, spellLine, target));
        }

        /// <summary>
        /// Add a spell to the queue. If there are already 2 spells in the
        /// queue, remove the spell that the pet would cast next.
        /// </summary>
        /// <param name="spell">The spell to add.</param>
        /// <param name="spellLine">The spell line the spell is in.</param>
        /// <param name="target">The target to cast the spell on.</param>
        private void AddToAttackSpellQueue(Spell spell, SpellLine spellLine, GameLiving target)
        {
            SpellQueueEntry spellQueueEntry = null;

            while (m_attackSpellQueue.Count >= 2)
                m_attackSpellQueue.TryDequeue(out spellQueueEntry);

            if (spellQueueEntry != null)
                MessageToHumanOwner("AI.Brain.Necromancer.SpellNoLongerInQueue", eChatType.CT_Spell, spellQueueEntry.Spell.Name, Body.Name);

            DebugMessageToOwner(string.Format("Adding spell '{0}' to the end of the queue", spell.Name));
            m_attackSpellQueue.Enqueue(new SpellQueueEntry(spell, spellLine, target));
        }

        #endregion

        #region Tether

        private const int SOFT_TETHER_RANGE = 1500;
        private const int HARD_TETHER_RANGE = 2000;
        private TetherTimer _tetherTimer = null;

        private void CheckTether()
        {
            // Bot movement is server-controlled: never kill a living servant
            // merely because its owner/path temporarily falls behind. Keep
            // the existing controlled-brain follow/combat behavior. World
            // removal and stable travel still perform their normal cleanup.
            // Human players retain the original distance warning and cutoff.
            if (Owner is GameBot)
            {
                _tetherTimer?.Stop();
                _tetherTimer = null;
                return;
            }

            // Check if pet is past hard tether range. If so, cut it right away.
            if (!Body.IsWithinRadius(Owner, HARD_TETHER_RANGE))
            {
                _tetherTimer?.Stop();
                (Body as NecromancerPet).CutTether();
                return;
            }

            // Check if pet is out of soft tether range.
            if (!Body.IsWithinRadius(Owner, SOFT_TETHER_RANGE))
                _tetherTimer ??= new(Body as NecromancerPet);
            else
            {
                if (_tetherTimer != null)
                {
                    // Pet is back in range, stop the timer.
                    _tetherTimer.OnReturnWithinRange();
                    _tetherTimer = null;
                }
            }
        }

        /// <summary>
        /// Timer for pet out of tether range.
        /// </summary>
        private class TetherTimer : ECSGameTimerWrapperBase
        {
            private NecromancerPet _pet;
            private GamePlayer _playerOwner;
            public int SecondsRemaining { get; set; } = 10;

            public TetherTimer(NecromancerPet pet) : base(pet)
            {
                _pet = pet;
                _playerOwner = pet.Owner as GamePlayer;
                Start(0);
            }

            protected override int OnTick(ECSGameTimer timer)
            {
                if (SecondsRemaining > 0)
                {
                    OutOfTetherCheck();
                    SecondsRemaining -= 1;
                    return 1000;
                }

                Stop();

                if (_playerOwner != null)
                    MessageToOwner(LanguageMgr.GetTranslation(_playerOwner.Client.Account.Language, "AI.Brain.Necromancer.HaveLostBondToPet"), eChatType.CT_System, _playerOwner);

                _pet.CutTether();
                return 0;

                void OutOfTetherCheck()
                {
                    // Pet past its tether, update effect icon (remaining time) and send warnings to owner at t = 10 seconds and t = 5 seconds.
                    SetShadeIconRemainingTime(SecondsRemaining);

                    if (_playerOwner == null)
                        return;

                    if (SecondsRemaining == 10)
                        MessageToOwner(LanguageMgr.GetTranslation(_playerOwner.Client.Account.Language, "AI.Brain.Necromancer.PetTooFarBeLostSecIm", SecondsRemaining), eChatType.CT_System, _playerOwner);
                    else if (SecondsRemaining == 5)
                        MessageToOwner(LanguageMgr.GetTranslation(_playerOwner.Client.Account.Language, "AI.Brain.Necromancer.PetTooFarBeLostSec", SecondsRemaining), eChatType.CT_System, _playerOwner);
                }
            }

            public void OnReturnWithinRange()
            {
                SetShadeIconRemainingTime(-1);
                Stop();
            }

            private void SetShadeIconRemainingTime(int duration)
            {
                if (_playerOwner == null)
                    return;

                if (EffectListService.GetEffectOnTarget(_playerOwner, eEffect.Shade) is not NecromancerShadeECSGameEffect shadeEffect)
                    return;

                shadeEffect.SetTetherTimer(duration);
                ECSGameEffect[] effectList = [shadeEffect];
                int effectsCount = 1;
                _playerOwner.Out.SendUpdateIcons(effectList, ref effectsCount);
            }
        }

        /// <summary>
        /// Send a message to the shade.
        /// </summary>
        public static void MessageToOwner(string message, eChatType chatType, GamePlayer owner)
        {
            if ((owner != null) && (message.Length > 0))
                owner.Out.SendMessage(message, chatType, eChatLoc.CL_SystemWindow);
        }

        private void MessageToHumanOwner(string translationKey, eChatType chatType, params object[] arguments)
        {
            if (Owner is not GamePlayer playerOwner)
                return;
            MessageToOwner(
                LanguageMgr.GetTranslation(playerOwner.Client.Account.Language, translationKey, arguments),
                chatType,
                playerOwner);
        }

        /// <summary>
        /// For debugging purposes only.
        /// </summary>
        private void DebugMessageToOwner(string message)
        {
            if (GS.ServerProperties.Properties.ENABLE_DEBUG)
            {
                long tick = GameLoop.GameLoopTime;
                long seconds = tick / 1000;
                long minutes = seconds / 60;

                MessageToOwner(string.Format("[{0:00}:{1:00}.{2:000}] {3}", minutes % 60, seconds % 60, tick % 1000, message), eChatType.CT_Staff, Owner as GamePlayer);
            }
        }

        #endregion
    }
}
