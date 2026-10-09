using DOL.AI.Brain;
using DOL.GS.Keeps;
using DOL.GS.ServerProperties;
using DOL.GS.Styles;
using DOL.Database;
using static DOL.GS.GameObject;

namespace DOL.GS
{
    public class NpcAttackAction : AttackAction, ILosCheckListener
    {
        private const double TIME_TO_TARGET_THRESHOLD_BEFORE_RANGED_SWITCH = 500; // NPCs will switch to ranged if further than melee range + (this * maxSpeed * 0.001).

        private GameNPC _npcOwner;
        private bool _hasLos;
        private CheckLosTimer _checkLosTimer;
        private GameObject _losCheckTarget;
        private bool _wasMeleeWeaponSwitchForced; // Used to prevent NPCs from switching to their ranged weapon automatically if they explicitly switched to a melee weapon during combat.

        private static int LosCheckInterval => Properties.CHECK_LOS_DURING_RANGED_ATTACK_MINIMUM_INTERVAL;
        private bool IsArcherGuardOrImmobile => _npcOwner is GuardArcher || _npcOwner.MaxSpeedBase == 0;

        public NpcAttackAction(GameNPC owner) : base(owner)
        {
            _npcOwner = owner;
        }

        public override void OnAimInterrupt(GameObject attacker)
        {
            // Use the follow target or current target (maybe redundant) instead of the interrupter.
            // We really don't want guards to move because a pet attacked them in melee.
            GameObject target = _npcOwner.TargetObject ?? _npcOwner.FollowTarget;

            if (target is not GameLiving livingFollowTarget)
                return;

            if (IsArcherGuardOrImmobile &&
                (livingFollowTarget.ActiveWeaponSlot is eActiveWeaponSlot.Distance || !livingFollowTarget.IsWithinRadius(_npcOwner, livingFollowTarget.attackComponent.AttackRange)))
            {
                _npcOwner.StopFollowing();
                return;
            }

            SwitchToMeleeAndTick();
        }

        public override void OnForcedWeaponSwitch()
        {
            switch (_npcOwner.ActiveWeaponSlot)
            {
                case eActiveWeaponSlot.Standard:
                case eActiveWeaponSlot.TwoHanded:
                {
                    _wasMeleeWeaponSwitchForced = true;
                    break;
                }
                case eActiveWeaponSlot.Distance:
                {
                    _wasMeleeWeaponSwitchForced = false;
                    break;
                }
            }
        }

        public override bool OnOutOfRangeOrNoLosRangedAttack()
        {
            // If we're a guard or an immobile NPC, let's forget about our target so that we can attack another one and not stare at the wall.
            // Otherwise, switch to melee, but keep the timer alive.
            if (IsArcherGuardOrImmobile)
            {
                StandardMobBrain brain = _npcOwner.Brain as StandardMobBrain;

                if (_losCheckTarget is GameLiving livingLosCheckTarget)
                    brain.RemoveFromAggroList(livingLosCheckTarget);

                brain.AttackMostWanted(); // This won't immediately start the attack on the new target, but we can use `TargetObject` to start checking it.
                GameObject nextTarget = _npcOwner.TargetObject;

                if (nextTarget != _losCheckTarget)
                    _checkLosTimer?.ChangeTarget(nextTarget); // The timer might be already cleaned up if this was the last target.

                return true;
            }
            else if (AttackComponent.AttackState && !_hasLos)
            {
                SwitchToMeleeAndTick();
                return true;
            }

            return false;
        }

        protected override bool PrepareMeleeAttack()
        {
            // Check spells before attacking to allow spell casting opportunity.
            // The NPC service's think cycles are not synchronized with attack cycles,
            // so without this, melee-attacking NPCs cannot reliably cast spells.
            if (_npcOwner.Brain is NecromancerPetBrain necroBrain)
            {
                if (necroBrain.CheckSpellQueue())
                    return false;
            }
            else if (_npcOwner.Brain is StandardMobBrain brain)
            {
                if (brain.CheckSpells(StandardMobBrain.eCheckSpellType.Offensive))
                {
                    _npcOwner.StopAttack();
                    return false;
                }
            }
            else if (_npcOwner.Brain is BotBrain botBrain)
            {
                if (botBrain.CheckSpells(BotBrain.eCheckSpellType.Offensive))
                {
                    _npcOwner.StopAttack();
                    return false;
                }
            }

            if (!_npcOwner.IsAttacking)
                return false;

            int meleeAttackRange = _npcOwner.MeleeAttackRange;
            int maxSpeed = _npcOwner.MaxSpeed;

            if (maxSpeed > 0)
                meleeAttackRange += (int) (TIME_TO_TARGET_THRESHOLD_BEFORE_RANGED_SWITCH * maxSpeed * 0.001);

            // NPCs try to switch to their ranged weapon whenever possible.
            DbInventoryItem distanceWeapon = _npcOwner.Inventory?.GetItem(eInventorySlot.DistanceWeapon);
            bool isGameBot = _npcOwner is GameBot;
            GameBot rangedBot = _npcOwner as GameBot;
            eCharacterClass botClass = rangedBot?.CharacterClass != null
                ? (eCharacterClass)rangedBot.CharacterClass.ID
                : eCharacterClass.Unknown;
            bool botCanUseRanged = rangedBot != null && BotRangedCombat.CanUse(rangedBot, distanceWeapon);
            if (!_npcOwner.IsBeingInterrupted &&
                distanceWeapon != null &&
                BotRangedCombat.AllowsAutomaticNpcRangedSwitch(isGameBot, botClass, botCanUseRanged) &&
                !_npcOwner.IsWithinRadius(_target, meleeAttackRange) &&
                !_wasMeleeWeaponSwitchForced)
            {
                // But only if there is no timer running or if it has LoS on its current target.
                // If the timer is running, it'll check for LoS continuously.
                if (!Properties.CHECK_LOS_BEFORE_NPC_RANGED_ATTACK || _checkLosTimer == null || !_checkLosTimer.IsAlive)
                {
                    SwitchToRangedAndTick();
                    return false;
                }

                if (_losCheckTarget != _target)
                {
                    _hasLos = false;
                    _checkLosTimer.ChangeTarget(_target);
                }
                else if (_hasLos)
                {
                    SwitchToRangedAndTick();
                    return false;
                }
            }

            _combatStyle = _npcOwner is GameBot meleeBot
                ? BotMeleeStylePolicy.Select(meleeBot, LastAttackData)
                : StyleComponent.GetStyleToUse();

            if (_npcOwner is GameBot styleBot && _combatStyle != null)
            {
                DbInventoryItem styleWeapon = (eObjectType)_combatStyle.WeaponTypeRequirement == eObjectType.Shield
                    ? _leftWeapon : _weapon;
                if (!StyleProcessor.CheckEnduranceCost(styleBot, styleWeapon, _combatStyle))
                    _combatStyle = null;
                // A shield is only the required item for validating a shield
                // style.  It is not the damaging weapon for the swing.  Keep
                // _weapon on the bot's active main-hand weapon so shield styles
                // do not feed a zero-DPS shield into WeaponAction and report
                // an apparent 0-damage attack.  This applies to both temporary
                // companions and autonomous GameBots.
            }

            if (!base.PrepareMeleeAttack())
                return false;

            // The target isn't in melee range yet. Check if another target is in range to attack on the way to the main target.
            if (!_npcOwner.IsWithinRadius(_target, meleeAttackRange) &&
                _npcOwner.Brain is not IControlledBrain &&
                _npcOwner.Brain is StandardMobBrain npcBrain)
            {
                _target = npcBrain.LastHighestThreatInAttackRange;

                if (_target == null || !_npcOwner.IsWithinRadius(_target, meleeAttackRange))
                {
                    _interval = TICK_INTERVAL_FOR_NON_ATTACK;
                    return false;
                }
            }

            return true;
        }

        protected override bool PrepareRangedAttack()
        {
            if (_npcOwner is GameBot bot)
            {
                // Never feed a visual-only NPC item (zero DPS/delay) into the
                // player damage pipeline, nor let an instrument become a bow.
                if (!BotRangedCombat.CanUse(bot, _weapon) || bot.Endurance < bot.rangeAttackComponent.ShotEnduranceCost)
                {
                    _interval = TICK_INTERVAL_FOR_NON_ATTACK;
                    bot.StopAttack();
                    SwitchToMeleeAndTick();
                    return false;
                }
                bot.rangeAttackComponent.UpdateAmmo(_weapon);
            }
            if (Properties.CHECK_LOS_BEFORE_NPC_RANGED_ATTACK)
            {
                if (_checkLosTimer == null)
                    _checkLosTimer = new(_npcOwner, _target, this);
                else if (_losCheckTarget != _target)
                {
                    _hasLos = false;
                    _checkLosTimer.ChangeTarget(_target);
                }

                if (!_hasLos)
                {
                    _interval = TICK_INTERVAL_FOR_NON_ATTACK;
                    return false;
                }
            }
            else
                _hasLos = true;

            return base.PrepareRangedAttack();
        }

        public override void CleanUp()
        {
            if (_npcOwner.Brain is NecromancerPetBrain necroBrain)
                necroBrain.CheckSpellQueue();

            if (_checkLosTimer != null)
            {
                _checkLosTimer.Stop();
                _checkLosTimer = null;
            }

            _wasMeleeWeaponSwitchForced = false;
            base.CleanUp();
        }

        public void HandleLosCheckResponse(GamePlayer player, LosCheckResponse response, ushort targetId)
        {
            _losCheckTarget = _npcOwner.CurrentRegion.GetObject(targetId);

            if (_losCheckTarget == null || _losCheckTarget != _target)
                _hasLos = false;
            else
                _hasLos = response is LosCheckResponse.True;

            if (!_hasLos)
            {
                OnOutOfRangeOrNoLosRangedAttack();
                return;
            }
        }

        private void SwitchToMeleeAndTick()
        {
            if (_npcOwner.ActiveWeaponSlot is not eActiveWeaponSlot.Distance)
                return;

            _npcOwner.StartAttackWithMeleeWeapon(_target);
        }

        private void SwitchToRangedAndTick()
        {
            if (_npcOwner.ActiveWeaponSlot is eActiveWeaponSlot.Distance)
                return;

            _npcOwner.StartAttackWithRangedWeapon(_target);
        }

        private void ForceLos()
        {
            // Ownerless autonomous GameBots have no client that can answer a
            // LOS request. Keep the accepted target in the same field used by
            // subsequent ranged ticks; leaving it null made every tick look
            // like a target change even after LOS had been accepted.
            _losCheckTarget = _target;
            _hasLos = true;
            _npcOwner.TurnTo(_target);
        }

        public class CheckLosTimer : ECSGameTimerWrapperBase
        {
            private GameNPC _npcOwner;
            private GameObject _target;
            private NpcAttackAction _attackAction;
            private GamePlayer _losChecker;

            public CheckLosTimer(GameObject owner, GameObject target, NpcAttackAction attackAction) : base(owner)
            {
                _npcOwner = owner as GameNPC;
                _attackAction = attackAction;
                ChangeTarget(target);
            }

            public void ChangeTarget(GameObject newTarget)
            {
                if (newTarget == null)
                {
                    Stop();
                    return;
                }

                if (_target != newTarget)
                {
                    _target = newTarget;

                    if (_npcOwner.Brain is IControlledBrain brain)
                        _losChecker = brain.GetPlayerOwner();
                    if (_target is GamePlayer targetPlayer)
                        _losChecker = targetPlayer;
                    else if (_target is GameNPC npcTarget && npcTarget.Brain is IControlledBrain targetBrain)
                        _losChecker = targetBrain.GetPlayerOwner();
                }

                // Don't bother starting the timer if there's no one to perform the LoS check.
                if (_losChecker == null)
                {
                    _attackAction.ForceLos();
                    return;
                }

                if (!IsAlive && _losChecker != null)
                {
                    Start(1);
                    Interval = LosCheckInterval;
                }
            }

            protected override int OnTick(ECSGameTimer timer)
            {
                // We normally rely on `AttackActon.CleanUp()` to stop this timer.
                if (!_npcOwner.attackComponent.AttackState || _npcOwner.ObjectState is not eObjectState.Active)
                    return 0;

                // Run 24: the checker (a player or a pet's player owner) can be gone by the time the timer ticks
                // (NullReferenceException in TimerService); fall back to the forced LoS like "no checker" above.
                if (_losChecker?.Out == null || _target == null)
                {
                    _attackAction.ForceLos();
                    return 0;
                }
                _losChecker.Out.SendLosCheckRequest(_npcOwner, _target, _attackAction);
                return LosCheckInterval;
            }
        }
    }
}
