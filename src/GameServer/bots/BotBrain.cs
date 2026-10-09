using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.GS;
using DOL.GS.Effects;
using DOL.GS.PacketHandler;
using DOL.GS.RealmAbilities;
using DOL.GS.SkillHandler;
using DOL.GS.Spells;
using DOL.GS.Styles;
using DOL.Logging;

namespace DOL.AI.Brain
{
    public partial class BotBrain : ABrain, IOldAggressiveBrain, IControlledBrain
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        public GameBot BotBody => Body as GameBot;
        private long _nextPoisonSupplyTick;

        #region IControlledBrain Implementation

        public GameLiving Owner => BotBody?.Owner;

        public eWalkState WalkState { get; protected set; } = eWalkState.Follow;

        public eAggressionState AggressionState { get; set; } = eAggressionState.Defensive;

        public bool IsMainPet { get; set; } = true;

        public virtual void Attack(GameObject target)
        {
            if (AggressionState == eAggressionState.Passive)
                AggressionState = eAggressionState.Defensive;

            if (target is GameLiving livingTarget && livingTarget != Body.TargetObject)
            {
                AddToAggroList(livingTarget, 1);
                FSM.SetCurrentState(eFSMStateType.AGGRO);
                // An attack/pull order changes the next decision target. It
                // must not cancel a legal cast already in flight; repeated
                // assist or pet-related orders otherwise make list casters
                // interrupt themselves forever.
                AttackMostWanted();
            }
        }

        public virtual void Disengage()
        {
            _orderedPullTarget = null;
            _returnToFormationAfterPull = false;
            ClearAggroList();
            FSM.SetCurrentState(eFSMStateType.FOLLOW);
        }

        public virtual void CheckAggressionStateOnPlayerOrder()
        {
            // Bots use their own AI logic, aggression state is managed by FSM
        }

        public virtual void Follow(GameObject target)
        {
            WalkState = eWalkState.Follow;
            if (target != null)
                Body.Follow(target, BotManager.FOLLOW_DISTANCE, BotManager.MAX_FOLLOW_DISTANCE);
        }

        public virtual void FollowOwner()
        {
            Follow(BotBody?.Owner);
        }

        public virtual void Stay()
        {
            WalkState = eWalkState.Stay;
            Body.StopMoving();
            FSM.SetCurrentState(eFSMStateType.IDLE);
        }

        public virtual void ComeHere()
        {
            if (BotBody?.Owner != null)
            {
                WalkState = eWalkState.ComeHere;
                Body.StopFollowing();
                Body.PathTo(BotBody.Owner, Body.MaxSpeed);
            }
        }

        public virtual void Goto(GameObject target)
        {
            if (target != null)
            {
                WalkState = eWalkState.GoTarget;
                Body.StopFollowing();
                Body.PathTo(target, Body.MaxSpeed);
            }
        }

        public virtual void UpdatePetWindow()
        {
            // Bots don't use pet windows - this is for player-controlled pets
        }

        public virtual GamePlayer GetPlayerOwner()
        {
            return BotBody?.Owner;
        }

        public virtual GameNPC GetNPCOwner()
        {
            return null; // Bots are always owned by players
        }

        public virtual GameLiving GetLivingOwner()
        {
            return BotBody?.Owner;
        }

        public virtual void SetAggressionState(eAggressionState state)
        {
            AggressionState = state;
        }

        #endregion

        #region IOldAggressiveBrain

        public int AggroLevel { get; set; } = 100;
        public int AggroRange { get; set; } = 3600;

        #endregion

        #region Aggro List

        private GameLiving _recentDirectAttacker;
        private long _recentDirectAttackTick;

        public const int MAX_AGGRO_DISTANCE = 3600;
        public const int MAX_AGGRO_LIST_DISTANCE = 6000;
        private const int EFFECTIVE_AGGRO_AMOUNT_CALCULATION_DISTANCE_THRESHOLD = 500;

        protected ConcurrentDictionary<GameLiving, AggroAmount> AggroList { get; private set; } = new();
        protected List<(GameLiving, long)> OrderedAggroList { get; private set; } = [];
        public GameLiving LastHighestThreatInAttackRange { get; private set; }

        public class AggroAmount(long @base = 0)
        {
            public long Base { get; set; } = @base;
            public long Effective { get; set; }
        }

        public virtual bool HasAggro => !AggroList.IsEmpty;

        private GameLiving _committedDungeonPull;
        private long _committedDungeonPullUntil;

        public void CommitDungeonPull(GameLiving target)
        {
            if (BotBody?.IsAutonomousWorldBot != true || BotBody.IsPlayerLedGroup ||
                Body.CurrentZone?.IsDungeon != true || target?.IsAlive != true)
                return;
            // Native combat's six-second idle timeout is shorter than travel
            // plus a first cast. Give a deliberately selected corridor pull a
            // bounded approach window; ordinary aggro and watchdogs still own
            // real combat, deaths and genuinely unreachable targets.
            _committedDungeonPull = target;
            _committedDungeonPullUntil = GameLoop.GameLoopTime + 30_000;
        }

        public static bool RetainCommittedDungeonPull(bool alive, bool sameRegion, bool inAggro,
            long now, long until) => alive && sameRegion && inAggro && now < until;

        private bool HasCommittedDungeonPull => _committedDungeonPull != null &&
            RetainCommittedDungeonPull(_committedDungeonPull.IsAlive,
                _committedDungeonPull.CurrentRegion == Body.CurrentRegion,
                AggroList.ContainsKey(_committedDungeonPull), GameLoop.GameLoopTime, _committedDungeonPullUntil);

        public virtual void AddToAggroList(GameLiving living, long aggroAmount)
        {
            if (Body.IsConfused || !Body.IsAlive || living == null)
                return;

            ForceAddToAggroList(living, aggroAmount);
        }

        public void ForceAddToAggroList(GameLiving living, long aggroAmount)
        {
            if (!CompanionEngagementMode.Allows(Body, living)) return;
            if (_autonomousWorldController?.IgnoresTarget(living) == true) return;
            if (CompanionRaidSiege.Ignores(Body as GameBot, living)) return;
            if (living is DOL.GS.Keeps.GuardLord { ShieldedFromBots: true }) return;
            if (aggroAmount > 0)
            {
                foreach (ProtectECSGameEffect protect in living.effectListComponent.GetAbilityEffects().Where(e => e.EffectType is eEffect.Protect))
                {
                    if (protect.Target != living)
                        continue;

                    GameLiving protectSource = protect.Source;

                    if (protectSource.IsIncapacitated || protectSource.IsSitting)
                        continue;

                    if (!living.IsWithinRadius(protectSource, ProtectAbilityHandler.PROTECT_DISTANCE))
                        continue;

                    int abilityLevel = protectSource.GetAbilityLevel(Abilities.Protect);
                    long protectAmount = (long)(abilityLevel * 0.1 * aggroAmount);

                    if (protectAmount > 0)
                    {
                        aggroAmount -= protectAmount;

                        if (protectSource is GamePlayer playerProtectSource)
                        {
                            playerProtectSource.Out.SendMessage(
                                $"You absorb {protectAmount} aggro from {living.GetName(0, false)} for {Body.GetName(0, false)}!",
                                eChatType.CT_System, eChatLoc.CL_SystemWindow);
                        }

                        AggroList.AddOrUpdate(protectSource, Add, Update, protectAmount);
                    }
                }
            }

            AggroList.AddOrUpdate(living, Add, Update, aggroAmount);

            if (living is IGamePlayer player)
            {
                if (player.Group != null)
                {
                    foreach (GamePlayer playerInGroup in player.Group.GetPlayersInTheGroup())
                    {
                        if (playerInGroup != living)
                            AggroList.TryAdd((GameLiving)playerInGroup, new());
                    }
                }
            }

            static AggroAmount Add(GameLiving key, long arg)
            {
                return new(Math.Max(0, arg));
            }

            static AggroAmount Update(GameLiving key, AggroAmount oldValue, long arg)
            {
                oldValue.Base = Math.Max(0, oldValue.Base + arg);
                return oldValue;
            }
        }

        public virtual void RemoveFromAggroList(GameLiving living)
        {
            AggroList.TryRemove(living, out _);
        }

        public long GetBaseAggroAmount(GameLiving living)
        {
            return AggroList.TryGetValue(living, out AggroAmount aggroAmount) ? aggroAmount.Base : 0;
        }

        public virtual void ClearAggroList()
        {
            _committedDungeonPull = null;
            _committedDungeonPullUntil = 0;
            AggroList.Clear();

            lock (((ICollection)OrderedAggroList).SyncRoot)
            {
                OrderedAggroList.Clear();
            }

            LastHighestThreatInAttackRange = null;
        }

        protected virtual bool ShouldBeRemovedFromAggroList(GameLiving living)
        {
            return !living.IsAlive ||
                   living.ObjectState != GameObject.eObjectState.Active ||
                   living.CurrentRegion != Body.CurrentRegion ||
                   !Body.IsWithinRadius(living, MAX_AGGRO_LIST_DISTANCE) ||
                   (!GameServer.ServerRules.IsAllowedToAttack(Body, living, true) && !living.effectListComponent.ContainsEffectForEffectType(eEffect.Shade));
        }

        protected virtual bool ShouldBeIgnoredFromAggroList(GameLiving living)
        {
            return living.effectListComponent.ContainsEffectForEffectType(eEffect.Shade);
        }

        protected virtual GameLiving CleanUpAggroListAndGetHighestModifiedThreat()
        {
            OrderedAggroList.Clear();

            int attackRange = Body.attackComponent.AttackRange;
            GameLiving highestThreat = null;
            KeyValuePair<GameLiving, AggroAmount> currentTarget = default;
            long highestEffectiveAggro = -1;
            long highestEffectiveAggroInAttackRange = -1;

            foreach (var pair in AggroList)
            {
                GameLiving living = pair.Key;

                if (Body.TargetObject == living)
                    currentTarget = pair;

                if (ShouldBeRemovedFromAggroList(living))
                {
                    AggroList.TryRemove(living, out _);
                    continue;
                }

                if (ShouldBeIgnoredFromAggroList(living))
                    continue;

                AggroAmount aggroAmount = pair.Value;
                double distance = Body.GetDistanceTo(living);
                aggroAmount.Effective = distance > EFFECTIVE_AGGRO_AMOUNT_CALCULATION_DISTANCE_THRESHOLD ?
                                        (long)Math.Ceiling(aggroAmount.Base * (EFFECTIVE_AGGRO_AMOUNT_CALCULATION_DISTANCE_THRESHOLD / distance)) :
                                        aggroAmount.Base;

                if (aggroAmount.Effective > highestEffectiveAggroInAttackRange)
                {
                    if (distance <= attackRange)
                    {
                        highestEffectiveAggroInAttackRange = aggroAmount.Effective;
                        LastHighestThreatInAttackRange = living;
                    }

                    if (aggroAmount.Effective > highestEffectiveAggro)
                    {
                        highestEffectiveAggro = aggroAmount.Effective;
                        highestThreat = living;
                    }
                }
            }

            if (highestThreat != null)
            {
                if (currentTarget.Key != null && currentTarget.Key != highestThreat && currentTarget.Value.Effective >= highestEffectiveAggro)
                    highestThreat = currentTarget.Key;
            }
            else
            {
                return AggroList.FirstOrDefault().Key?.ControlledBrain?.Body;
            }

            return highestThreat;
        }

        protected virtual GameLiving CalculateNextAttackTarget()
        {
            return CleanUpAggroListAndGetHighestModifiedThreat();
        }

        public virtual bool CanAggroTarget(GameLiving target)
        {
            if (!CompanionEngagementMode.Allows(Body, target)) return false;
            if (!GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                return false;

            GameLiving realTarget = target;

            if (realTarget is GameNPC npcTarget
                && npcTarget.Brain is IControlledBrain npcTargetBrain
                && npcTargetBrain.GetLivingOwner() is GameLiving livingOwner)
            {
                realTarget = livingOwner;
            }

            if (realTarget is IGamePlayer && realTarget.Realm != Body.Realm)
                return true;

            // Evaluate the monster from the character's perspective, not the
            // reverse. The native NPC check rejected purple pulls because the
            // character was grey to the monster. Camp selection owns difficulty;
            // this shared execution gate only excludes non-XP grey monsters.
            if (Body.IsObjectGreyCon(realTarget))
                return false;

            return AggroLevel > 0;
        }

        /// <summary>
        /// Retaliation is not a new XP pull. A grey creature that actually hit
        /// the bot (or is already fighting its pet) must still be defendable.
        /// Keep proactive camp/pull selection on CanAggroTarget, while this
        /// narrower gate checks only live attack legality.
        /// </summary>
        private bool CanDefendAgainst(GameLiving target) =>
            target?.IsAlive == true &&
            CompanionEngagementMode.Allows(Body, target) &&
            target.ObjectState == GameObject.eObjectState.Active &&
            target.CurrentRegion == Body.CurrentRegion &&
            GameServer.ServerRules.IsAllowedToAttack(Body, target, true);

        public virtual void OnAttackedByEnemy(AttackData ad)
        {
            CompanionPvpEngagement.RecordThreat(BotBody, Body, ad);
            if (!Body.IsAlive || Body.ObjectState != GameObject.eObjectState.Active || FSM.GetCurrentState() == FSM.GetState(eFSMStateType.PASSIVE))
                return;

            if (ad.GeneratesAggro)
            {
                if (BotBody?.IsRecoveryResting == true)
                    BotBody.WakeRecoveryRest();
                ConvertDamageToAggroAmount(ad.Attacker, Math.Max(1, ad.Damage + ad.CriticalDamage));
                if (ad.Attacker?.IsAlive == true && ad.Attacker != Body)
                {
                    _recentDirectAttacker = ad.Attacker;
                    _recentDirectAttackTick = GameLoop.GameLoopTime;
                }
                // A remote efficient bot may otherwise spend its one immediate
                // wake-up merely changing IDLE/FOLLOW to AGGRO, then wait for a
                // second background planning interval before retaliating. Enter
                // combat here, exactly as StandardMobBrain does when aggro is
                // added, so the first woken pulse executes the combat state.
                if (ShouldEnterAggroStateAfterDirectAttack(
                        HasAggro,
                        FSM.GetCurrentState() == FSM.GetState(eFSMStateType.AGGRO)))
                    FSM.SetCurrentState(eFSMStateType.AGGRO);

                // Damage is an event, so wake combat immediately without
                // increasing the ordinary idle/travel AI cadence.
                NextThinkTick = GameLoop.GameLoopTime;
            }
        }

        public static bool ShouldEnterAggroStateAfterDirectAttack(bool hasAggro, bool alreadyInAggroState) =>
            hasAggro && !alreadyInAggroState;

        protected virtual void ConvertDamageToAggroAmount(GameLiving attacker, int damage)
        {
            if (attacker is GameNPC NpcAttacker && NpcAttacker.Brain is ControlledMobBrain controlledBrain)
            {
                damage = controlledBrain.ModifyDamageWithTaunt(damage);

                int aggroForOwner = (int)(damage * 0.15);

                if (aggroForOwner == 0)
                {
                    AddToAggroList(controlledBrain.Owner, 1);
                    AddToAggroList(NpcAttacker, Math.Max(2, damage));
                }
                else
                {
                    AddToAggroList(controlledBrain.Owner, aggroForOwner);
                    AddToAggroList(NpcAttacker, damage - aggroForOwner);
                }
            }
            else
                AddToAggroList(attacker, damage);
        }

        public const int GROUP_DEFENSE_ASSIST_RADIUS = 2000;

        public static GameLiving GroupMemberForCombat(GameLiving living)
        {
            if (living is GamePlayer or GameBot) return living;
            // Only the actual main pet of a player/bot. Never walk through a
            // companion's Owner and accidentally attribute its pet to the human.
            if (living is GameNPC { Brain: ControlledMobBrain { Owner: GameLiving owner } } &&
                owner is GamePlayer or GameBot && owner.ControlledBrain?.Body == living)
                return owner;
            return living;
        }

        /// <summary>
        /// Wakes only nearby members of the victim's exact group. Being close by
        /// is never sufficient on its own, so solo bots and unrelated groups do
        /// not chain-aggro from another character's fight.
        /// </summary>
        public static void NotifyNearbyGroupBots(GameLiving victim, AttackData ad)
        {
            GameLiving groupMember = GroupMemberForCombat(victim);
            // Attached sub-pets also enlist their human-led raid in PvP. The
            // existing autonomous and PvE membership rules stay unchanged.
            GameLiving pvpMember = CompanionPvpEngagement.Character(victim);
            GamePlayer pvpLeader = pvpMember as GamePlayer ?? CompanionPvpEngagement.Leader(pvpMember);
            bool companionSubPet = pvpMember != groupMember && CompanionPvpEngagement.Enemy(pvpLeader, ad?.Attacker);
            if (companionSubPet) groupMember = pvpMember;
            Group group = groupMember?.Group;
            if (group == null || ad?.Attacker is not GameLiving attacker ||
                !attacker.IsAlive || !group.IsInTheGroup(groupMember))
                return;

            // Record the encounter synchronously so even a very short roadside
            // fight pauses autonomous travel for whole-party recovery afterward.
            AutonomousBotGroupCoordinator.MarkCombatObserved(group);

            foreach (GameLiving member in group.GetMembersInTheGroup())
            {
                if (member == victim || member is not GameBot bot ||
                    companionSubPet && CompanionPvpEngagement.Leader(bot) != pvpLeader ||
                    bot.Group != group || !group.IsInTheGroup(bot) ||
                    !bot.IsAlive || bot.ObjectState != GameObject.eObjectState.Active ||
                    bot.CurrentRegionID != victim.CurrentRegionID ||
                    !bot.IsWithinRadius(victim, GROUP_DEFENSE_ASSIST_RADIUS) ||
                    bot.Brain is not BotBrain brain)
                    continue;

                brain.OnGroupMemberAttacked(victim, ad);
            }
            if (groupMember is GameBot raidVictim)
                foreach (GameLiving member in AutonomousRealmRaid.ClaimNearbyDefenseBroadcast(raidVictim, GameLoop.GameLoopTime))
                    if (member is GameBot helper && helper.Group != group && helper.IsAlive && helper.ObjectState == GameObject.eObjectState.Active && !helper.IsOnStableMasterRoute &&
                        helper.CurrentRegionID == victim.CurrentRegionID && helper.IsWithinRadius(victim, GROUP_DEFENSE_ASSIST_RADIUS) &&
                        helper.Brain is BotBrain helperBrain)
                        helperBrain.OnGroupMemberAttacked(victim, ad);
        }

        public void OnGroupMemberAttacked(GameLiving victim, AttackData ad)
        {
            Group group = Body?.Group;
            GameLiving groupMember = GroupMemberForCombat(victim);
            if (CompanionPvpEngagement.RecordThreat(BotBody, victim, ad))
                groupMember = CompanionPvpEngagement.Character(victim);
            bool sisterParty = groupMember is GameBot other && other.Group != group &&
                AutonomousRealmRaid.SameExpedition(BotBody, other);
            if (group == null || groupMember == null || (!sisterParty && (groupMember.Group != group || !group.IsInTheGroup(groupMember))) ||
                !group.IsInTheGroup(Body) ||
                groupMember.CurrentRegionID != Body.CurrentRegionID ||
                victim.CurrentRegionID != Body.CurrentRegionID ||
                !Body.IsWithinRadius(victim, GROUP_DEFENSE_ASSIST_RADIUS) ||
                ad?.Attacker is not GameLiving attacker || !attacker.IsAlive ||
                !GameServer.ServerRules.IsAllowedToAttack(Body, attacker, true))
                return;
            if (sisterParty && (BotBody.IsOnStableMasterRoute || Body.CurrentZone == null || Body.CurrentZone != attacker.CurrentZone ||
                !PathfindingProvider.Instance.HasLineOfSight(Body.CurrentZone, new(Body.X,Body.Y,Body.Z),
                    new(attacker.X,attacker.Y,attacker.Z),PathfindingProvider.Instance.DefaultFilters))) return;

            switch (ad.AttackResult)
            {
                case eAttackResult.Blocked:
                case eAttackResult.Evaded:
                case eAttackResult.Fumbled:
                case eAttackResult.HitStyle:
                case eAttackResult.HitUnstyled:
                case eAttackResult.Missed:
                case eAttackResult.Parried:
                    PlayerLedPullCoordinator.OnGroupThreat(groupMember);
                    AutonomousDefensivePull.OnThreat(groupMember, attacker);
                    if (sisterParty) AutonomousDefensivePull.OnThreat(BotBody, attacker);
                    AddToAggroList(attacker, attacker.EffectiveLevel + ad.Damage + ad.CriticalDamage);
                    break;
            }

            if (HasAggro)
            {
                if (sisterParty) AutonomousBotGroupCoordinator.MarkCombatObserved(group);
                // Like a direct hit, a nearby group-defense event must wake a
                // resting bot now, not wait for its next idle planning turn.
                // Membership/range/attack legality were checked above.
                if (BotBody?.IsRecoveryResting == true)
                    BotBody.WakeRecoveryRest();
                NextThinkTick = GameLoop.GameLoopTime;
                if (FSM.GetState(eFSMStateType.AGGRO) != FSM.GetCurrentState())
                    FSM.SetCurrentState(eFSMStateType.AGGRO);
            }
        }

        #endregion

        #region Brain Core

        public bool IsHealer;
        public bool AlreadyCheckedHeals;
        private long _nextPriorityTauntTick;
        private long _nextDeployablePetTick;
        private long _nextCombatProgressTick;
        private long _nextMaintenanceBuffTick;
        private long _nextSongTwistTick;
        private bool _companionPerformerBuffBatch;
        private long _lastPerformerFollowTick = long.MinValue;
        private long _nextInstrumentKitCheck;
        private long _nextNecromancerCommandTick;
        private int _activeTwistedSongId;
        private readonly Dictionary<int, SpellLine> _knownSpellLines = new();
        private GamePlayer _subscribedAssistedPlayer;
        private GameLiving _orderedPullTarget;
        private bool _returnToFormationAfterPull;
        private GamePlayer _activityTrackedLeader;
        private int _leaderLastX;
        private int _leaderLastY;
        private int _leaderLastZ;
        private long _leaderLastActiveTick;
        private long _temporaryCompanionLastActionTick;
        private bool _ambientWanderMovement;
        private DateTime _lastIdleRoleplayUtc = DateTime.MinValue;
        private long _nextAmbientChatCheckTick;
        private long _nextExchangeCheckTick;
        private long _nextMerchantServiceTick;
        private long _pendingExchangeListTick;
        private DbInventoryItem _pendingExchangeItem;
        private int _pendingExchangePrice;
        private AutonomousWorldBotController _autonomousWorldController;
        private long _nextFidelityCheckTick;
        private long _nextNaturalMobWakeTick;
        private eAutonomousFidelity _cachedFidelity = eAutonomousFidelity.Efficient;
        public GamePlayer AssistedPlayer => BotBody?.PlayerGroupLeader ?? BotBody?.Owner;
        public override int ThinkInterval { get; set; } = 500;

        private const int LEADER_IDLE_WANDER_DELAY_MS = 30_000;
        private const int IDLE_WANDER_CYCLE_MS = 12_000;

        public BotBrain() : base()
        {
            FSM = new FSM();
            FSM.Add(new BotState_Idle(this));
            FSM.Add(new BotState_Follow(this));
            FSM.Add(new BotState_Aggro(this));
            FSM.Add(new BotState_Passive(this));
            FSM.SetCurrentState(eFSMStateType.IDLE);
        }

        public override bool Start()
        {
            if (!base.Start())
                return false;

            // Track the current player leader for formation and group defense.
            RebindAssistedPlayer();

            return true;
        }

        public override bool Stop()
        {
            // Unregister from owner attack events
            if (_subscribedAssistedPlayer != null)
                GameEventMgr.RemoveHandler(_subscribedAssistedPlayer, GameLivingEvent.AttackedByEnemy, new DOLEventHandler(OnOwnerAttacked));
            _subscribedAssistedPlayer = null;

            if (base.Stop())
            {
                ClearAggroList();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Called when the bot's owner is attacked by an enemy.
        /// Adds the attacker to the aggro list and transitions to AGGRO state.
        /// </summary>
        /// <param name="ad">The attack data</param>
        public virtual void OnOwnerAttacked(AttackData ad)
        {
            GamePlayer leader = AssistedPlayer;
            if (leader != null)
                OnGroupMemberAttacked(leader, ad);
        }

        public void RebindAssistedPlayer()
        {
            GamePlayer next = AssistedPlayer;
            if (_subscribedAssistedPlayer == next)
                return;
            if (_subscribedAssistedPlayer != null)
                GameEventMgr.RemoveHandler(_subscribedAssistedPlayer, GameLivingEvent.AttackedByEnemy, new DOLEventHandler(OnOwnerAttacked));
            _subscribedAssistedPlayer = next;
            ResetLeaderActivity(next);
            // GamePlayer.OnAttackedByEnemy performs one group-scoped broadcast.
            // Do not subscribe every bot independently or the same attack would
            // be processed twice by player-led companions.
        }

        public virtual bool OrderPull(GameLiving target)
        {
            if (target == null || !target.IsAlive || !CanAggroTarget(target))
                return false;

            if (BotBody?.IsTemporaryCompanionRestLocked == true)
                return false;

            if (BotPartyRoles.IsSupport(BotBody))
            {
                _orderedPullTarget = null;
                _returnToFormationAfterPull = false;
                ClearAggroList();
                Body.StopAttack();
                Body.TargetObject = null;
                if (!UsesDefensiveOnlyPet)
                    Body.ControlledBrain?.Disengage();
                FSM.SetCurrentState(eFSMStateType.FOLLOW);
                return false;
            }

            _orderedPullTarget = target;
            _returnToFormationAfterPull = BotBody?.IsPlayerLedGroup == true;
            AddToAggroList(target, Math.Max(100, target.EffectiveLevel * 10));
            FSM.SetCurrentState(eFSMStateType.AGGRO);
            AttackMostWanted();
            return true;
        }

        public void AssistPlayerAttack(GameLiving target)
        {
            CompanionEngagementMode.RememberPull(AssistedPlayer, target);
            if (!CompanionEngagementMode.Allows(Body, target)) return;
            if (CompanionPvpEngagement.Enemy(CompanionPvpEngagement.Leader(Body), target))
                BotBody.WakeTemporaryCompanionRest();
            if (BotPartyRoles.IsSupport(BotBody))
            {
                TryCommandCompanionDruidPet(target);
                HoldSupportCombat();
                NextThinkTick = GameLoop.GameLoopTime;
                return;
            }
            if (ActiveOrderedPullTarget != target || !HasAggro)
            {
                if (BotBody.IsRecoveryResting) BotBody.WakeRecoveryRest();
                OrderPull(target);
                NextThinkTick = GameLoop.GameLoopTime;
            }
        }

        public virtual void PrepareForTankPull()
        {
            _orderedPullTarget = null;
            _returnToFormationAfterPull = false;
            ClearAggroList();
            Body.StopAttack();
            Body.StopFollowing();
            Body.TargetObject = null;
            Body.ControlledBrain?.Disengage();
            Body.ControlledBrain?.Follow(Body);
            if (Body.IsCasting && Body.castingComponent?.SpellHandler?.Spell?.IsHarmful == true && !PvpControlInFlight)
                Body.StopCurrentSpellcast();
        }

        public virtual void CancelOrderedPull(GameLiving target)
        {
            if (_orderedPullTarget != target) return;
            _orderedPullTarget = null;
            _returnToFormationAfterPull = false;
            RemoveFromAggroList(target);
            if (!HasAggro)
            {
                Body.StopAttack();
                if (Body.TargetObject == target) Body.TargetObject = null;
                Body.ControlledBrain?.Follow(Body);
                FSM.SetCurrentState(eFSMStateType.FOLLOW);
            }
        }

        internal GameLiving ActiveOrderedPullTarget
        {
            get
            {
                if (_orderedPullTarget?.IsAlive == true &&
                    _orderedPullTarget.ObjectState is GameObject.eObjectState.Active &&
                    CanAggroTarget(_orderedPullTarget))
                    return _orderedPullTarget;

                _orderedPullTarget = null;
                return null;
            }
        }

        private bool UsesDefensiveOnlyPet => BotBody?.CharacterClass != null &&
            AutonomousPetSupport.IsDefensiveOnlyPetClass((eCharacterClass)BotBody.CharacterClass.ID, BotBody.Group?.MemberCount > 1);

        // Class role, not pet ownership: includes Runemaster/Wizard as well as
        // summoners. Melee hybrids and archers retain their own combat policy.
        public static bool PrefersSpellRange(ICharacterClass characterClass, bool grouped = false, bool autonomous = false) =>
            characterClass?.ID == (int)eCharacterClass.Minstrel && !grouped && !autonomous ||
            characterClass?.ClassType == eClassType.ListCaster &&
            characterClass.ID != (int)eCharacterClass.Valewalker &&
            characterClass.ID != (int)eCharacterClass.Vampiir &&
            // Shamans melee between their nukes (ShamanBotCombatPolicy).
            characterClass.ID != (int)eCharacterClass.Shaman;

        private bool PrefersCurrentSpellRange() =>
            PrefersSpellRange(BotBody?.CharacterClass, BotBody?.Group?.MemberCount > 1, BotBody?.IsAutonomousWorldBot == true);

        // True when the body already runs the same melee follow NpcStartAttack would
        // issue (its own target, melee weapon, stick distance).
        private bool IsMeleeChasing(GameObject target) =>
            target != null && Body.FollowTarget == target &&
            Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance &&
            Body.movementComponent.MinFollowDistance <= Body.StickMinimumRange;

        private bool UsesMinstrelHybridCombat => BotBody?.IsAutonomousWorldBot == true &&
            BotBody.CharacterClass?.ID == (int)eCharacterClass.Minstrel;

        private bool HoldsExclusiveSupportRole() =>
            BotPartyRoles.IsSupport(BotBody) &&
            ((eCharacterClass?)BotBody?.CharacterClass?.ID != eCharacterClass.Minstrel ||
             RecentDirectAttacker() is not GameLiving attacker ||
             !Body.IsWithinRadius(attacker, Body.MeleeAttackRange + 35));

        private int PlayerLedCasterEngagementRange(GameLiving target)
        {
            if (target == null || BotBody?.IsPlayerLedGroup != true ||
                !PrefersCurrentSpellRange())
                return 0;

            int range = OffensiveApproachRange(target);
            return Math.Max(0, range - 100);
        }

        private bool ApproachPlayerLedPullTarget(GameLiving target)
        {
            int range = PlayerLedCasterEngagementRange(target);
            if (range <= 0 || Body.IsWithinRadius(target, range))
                return false;

            // Walking into range is an intentional combat action; a resting bot
            // ignores follow orders.
            BotBody.WakeRecoveryRest();
            Body.StopAttack();
            if (!UsesDefensiveOnlyPet && LeaderIsFighting(target))
                CommandPetAttack(target);
            else
                Body.ControlledBrain?.Follow(Body);
            (int minDistance, int maxDistance) = PlayerLedApproachFollowDistances(range);
            Body.Follow(target, minDistance, maxDistance);
            return true;
        }

        /// <summary>
        /// Follow distances for walking a caster into range of a player-led
        /// target. The stop distance is just inside spell range. The maximum is
        /// how far away the target may be while we still chase it. It must not
        /// be the spell range: native follow gives up at once when the target
        /// is beyond the maximum, which left casters standing still whenever
        /// the target was more than a few units out of range.
        /// </summary>
        public static (int MinDistance, int MaxDistance) PlayerLedApproachFollowDistances(int engagementRange) =>
            (Math.Clamp(engagementRange - 100, 160, short.MaxValue), MAX_AGGRO_LIST_DISTANCE);

        /// <summary>True when the human leader, or the leader's own pet, is already fighting this target.</summary>
        private bool LeaderIsFighting(GameLiving target) =>
            target != null && PlayerLedPullCoordinator.FindLeaderTarget(AssistedPlayer) == target;

        /// <summary>
        /// The one combat target this bot's pet should have for this decision.
        /// Several owner systems (pull orders, protection, the last attacker,
        /// threat) can name different enemies in the same turn, and each changed
        /// order used to cancel the pet's current cast. The pet therefore keeps
        /// its current, still-valid target, and switches only when:
        /// - the owner has an explicit ordered pull target (a leader/pull
        ///   command), or
        /// - the current target has died, left, become illegal, or been mezzed, or
        /// - the candidate is attacking the owner while the current target is
        ///   attacking neither the owner nor the pet (the pet peels for its owner).
        /// None of these can alternate between two enemies.
        /// </summary>
        public GameLiving ResolvePetCombatTarget(GameLiving candidate)
        {
            if (candidate == null || Body.ControlledBrain is not ControlledMobBrain petBrain)
                return candidate;

            GameLiving current = petBrain.OrderedAttackTarget;
            if (current == null || current == candidate || candidate == ActiveOrderedPullTarget)
                return candidate;

            if (!IsValidPetCombatTarget(petBrain.Body, current))
                return candidate;

            bool candidateAttacksOwner = candidate.TargetObject == Body;
            bool currentAttacksOwnerOrPet = current.TargetObject == Body || current.TargetObject == petBrain.Body;
            return candidateAttacksOwner && !currentAttacksOwnerOrPet ? candidate : current;
        }

        public static bool IsValidPetCombatTarget(GameNPC pet, GameLiving target) =>
            pet != null && target?.IsAlive == true &&
            target.ObjectState == GameObject.eObjectState.Active &&
            target.CurrentRegion == pet.CurrentRegion &&
            !target.IsMezzed &&
            pet.IsWithinRadius(target, MAX_AGGRO_LIST_DISTANCE) &&
            CompanionEngagementMode.Allows(pet, target) &&
            GameServer.ServerRules.IsAllowedToAttack(pet, target, true);

        /// <summary>Orders the pet onto the resolved target; repeated identical orders are ignored by the pet brain.</summary>
        private void CommandPetAttack(GameLiving candidate)
        {
            GameLiving target = ResolvePetCombatTarget(candidate);
            if (target != null)
                Body.ControlledBrain?.Attack(target);
        }

        private void ResetLeaderActivity(GamePlayer leader)
        {
            _activityTrackedLeader = leader;
            _leaderLastX = leader?.X ?? 0;
            _leaderLastY = leader?.Y ?? 0;
            _leaderLastZ = leader?.Z ?? 0;
            _leaderLastActiveTick = GameLoop.GameLoopTime;
            _temporaryCompanionLastActionTick = GameLoop.GameLoopTime;
        }

        private void ObserveLeaderActivity()
        {
            GamePlayer leader = AssistedPlayer;
            if (leader == null || leader != _activityTrackedLeader)
            {
                ResetLeaderActivity(leader);
                return;
            }

            long dx = leader.X - _leaderLastX;
            long dy = leader.Y - _leaderLastY;
            long dz = leader.Z - _leaderLastZ;
            bool changedPosition = dx * dx + dy * dy + dz * dz >= 64;
            bool activelyPlaying = leader.IsMoving || changedPosition || leader.InCombat || leader.IsAttacking ||
                                   (leader.IsCasting && leader.castingComponent?.SpellHandler?.Spell?.IsHarmful == true);
            if (activelyPlaying)
                _leaderLastActiveTick = GameLoop.GameLoopTime;

            _leaderLastX = leader.X;
            _leaderLastY = leader.Y;
            _leaderLastZ = leader.Z;

            GameBot bot = BotBody;
            bool companionActing = bot?.IsTemporaryGroupHelper == true &&
                ((bot.IsMoving && !CanSettleForCompanionRest(bot, leader)) || bot.InCombat || bot.IsAttacking || HasAggro ||
                 ActiveOrderedPullTarget != null ||
                 (bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true) &&
                 !BotSongTwistPolicy.HasMobileSongCast(bot));
            if (companionActing)
                _temporaryCompanionLastActionTick = GameLoop.GameLoopTime;
        }

        internal void MarkTemporaryCompanionRestActivity()
        {
            if (BotBody?.IsTemporaryGroupHelper == true)
                _temporaryCompanionLastActionTick = GameLoop.GameLoopTime;
        }

        private static bool CanSettleForCompanionRest(GameBot bot, GamePlayer leader) =>
            bot != null && BotRestRecovery.CanSettleForRest(
                bot.IsTemporaryGroupHelper, bot.IsAutonomousWorldBot,
                leader?.IsAlive == true && bot.Group == leader.Group &&
                    bot.CurrentRegionID == leader.CurrentRegionID && bot.IsWithinRadius(leader, 350),
                leader?.IsMoving != false,
                BotRestRecovery.HasAnyResourceDeficit(bot.Health, bot.MaxHealth,
                    bot.Mana, bot.MaxMana, bot.Endurance, bot.MaxEndurance));

        private bool CanAmbientWander =>
            BotBody?.IsPlayerLedGroup == true &&
            AssistedPlayer?.IsAlive == true &&
            !BotBody.IsRecoveryResting &&
            !AutonomousRestPolicy.NeedsRecovery(
                Body.HealthPercent,
                Body.ManaPercent,
                Body.EndurancePercent,
                Body.MaxMana > 0) &&
            !AssistedPlayer.IsMoving &&
            !AssistedPlayer.InCombat &&
            !AssistedPlayer.IsAttacking &&
            GameLoop.GameLoopTime - _leaderLastActiveTick >= LEADER_IDLE_WANDER_DELAY_MS;

        public void EnforceCompanionEngagementRange()
        {
            if (CompanionEngagementMode.DefensiveLeader(Body) == null) return;
            foreach (GameLiving target in AggroList.Keys)
                if (!CompanionEngagementMode.Allows(Body, target)) RemoveFromAggroList(target);
            if (ActiveOrderedPullTarget is GameLiving order && !CompanionEngagementMode.Allows(Body, order))
                CancelOrderedPull(order);
            if (Body.TargetObject is GameLiving current && !CompanionEngagementMode.Allows(Body, current))
            {
                Body.StopAttack();
                Body.StopFollowing();
                if (Body.castingComponent?.SpellHandler?.Spell?.IsHarmful == true) Body.StopCurrentSpellcast();
                Body.TargetObject = null;
            }
        }

        private bool TryMaintainTemporaryCompanionRest()
        {
            GameBot bot = BotBody;
            if (bot?.IsTemporaryGroupHelper != true || bot.IsAutonomousWorldBot || !bot.IsPlayerLedGroup)
                return false;

            GamePlayer leader = AssistedPlayer;
            CabalistRestDiagnostics.Observe(bot, leader);
            bool atPlayer = leader?.IsAlive == true && bot.Group == leader.Group &&
                            leader.CurrentRegionID == bot.CurrentRegionID && bot.IsWithinRadius(leader, 350);
            bool movementBlocksRest = !CanSettleForCompanionRest(bot, leader) && (leader == null || AutonomousRestPolicy.MovementBlocksRest(
                bot.IsMoving,
                leader.IsMoving,
                _ambientWanderMovement));
            bool combatOrOrderBlocksRest = leader?.IsAttacking == true ||
                                           (leader?.IsCasting == true &&
                                            leader.castingComponent?.SpellHandler?.Spell?.IsHarmful == true) ||
                                           ActiveOrderedPullTarget != null ||
                                           PlayerLedPullCoordinator.IsWaiting(bot) ||
                                           BotRestRecovery.BlocksRest(bot);

            bool shouldRest = BotRestRecovery.ShouldTemporaryCompanionRest(
                true,
                atPlayer,
                movementBlocksRest,
                combatOrOrderBlocksRest,
                GameLoop.GameLoopTime,
                BotRestRecovery.LatestRestActivityTick(
                    _leaderLastActiveTick,
                    _temporaryCompanionLastActionTick),
                bot.Health,
                bot.MaxHealth,
                bot.Mana,
                bot.MaxMana,
                bot.Endurance,
                bot.MaxEndurance);

            if (!shouldRest)
            {
                bot.WakeTemporaryCompanionRest();
                return false;
            }

            // Stop the old formation order once the quiet/combat gates permit
            // recovery, including before upkeep queues a non-mobile buff.
            bot.StopFollowing();
            bot.StopMovingOnPath();
            bot.StopMoving();

            // Required class buffs and permanent-pet/Bonedancer-army upkeep get
            // one chance before the rest transition. This prevents the old
            // sit-wake-sit cycle between maintenance casts. A successful cast
            // records companion activity and must finish before a new two-second
            // quiet window can qualify the bot for rest.
            if (TryRunTemporaryCompanionRequiredMaintenance())
                return true;

            _ambientWanderMovement = false;
            bool resting = bot.BeginTemporaryCompanionRest();
            if (resting && IsClassicSongClass(bot))
                TryMaintainClassicSongTwist();
            return resting;
        }

        private bool TryRunTemporaryCompanionRequiredMaintenance()
        {
            GameBot bot = BotBody;
            if (bot?.IsTemporaryGroupHelper != true || !bot.IsAlive || bot.InCombat || HasAggro)
                return false;

            // A missing real class buff is required upkeep. The normal
            // selection/cost/cooldown checks remain authoritative; CastSpell
            // clears an existing rest lock only after one valid request is selected.
            if (TryMaintainTravelAndClassBuffs())
                return true;

            eCharacterClass characterClass = (eCharacterClass)bot.CharacterClass.ID;
            if (!AutonomousPetSupport.OwnsPetUpkeep(characterClass))
                return false;

            // Main pets and Bonedancer armies must not remain absent merely
            // because their owner is recovering. Animist combat turrets are not
            // in OwnsPetUpkeep and therefore cannot wake an idle resting bot.
            return AutonomousPetSupport.Maintain(
                bot,
                null,
                ref _nextDeployablePetTick,
                out _);
        }

        private bool TryMaintainTemporaryCompanionBonedancerArmy()
        {
            GameBot bot = BotBody;
            if (bot == null || HasAggro ||
                !AutonomousPetSupport.NeedsTemporaryCompanionBonedancerArmyUpkeep(bot))
            {
                return false;
            }

            // This gate exists only for ephemeral /spawn Bonedancers. It runs
            // before owner-follow/rest can indefinitely defer the level-15+
            // commander replacement and subordinate summon. Persistent
            // autonomous gamebots remain on their existing maintenance path.
            return AutonomousPetSupport.Maintain(
                bot,
                null,
                ref _nextDeployablePetTick,
                out _);
        }

        private void FollowFormation(bool ambientWander = false)
        {
            // Any FOLLOW entry or support path must honor a temporary caster's
            // non-mobile speed cast, even if the leader leaves IDLE range while
            // those three seconds are in progress.
            if (CompanionFollowPolicy.NativeTravelSpeedCastInProgress(BotBody))
                return;
            // The traveling-performer preflight may already have issued this
            // turn's path. Do not calculate a duplicate raid formation path.
            if (_lastPerformerFollowTick == GameLoop.GameLoopTime)
                return;
            if (!ambientWander)
                _ambientWanderMovement = false;

            GamePlayer leader = AssistedPlayer;
            if (leader == null || leader.CurrentRegion != Body.CurrentRegion)
            {
                _ambientWanderMovement = false;
                return;
            }

            bool tightInterior = Body.CurrentRegion?.IsDungeon == true;
            bool companionTravel = CompanionFollowPolicy.Applies(BotBody);
            // Do not run native owner-follow and formation orders concurrently.
            if (companionTravel) Body.StopFollowing();
            if (CompanionRaid.IsMember(BotBody))
            {
                // Stable, distinct slots for all 39 companions. Surface projection
                // and PathTo keep nearby slots from becoming walks through walls.
                // During a player keep siege a squad holds its own post instead (CompanionRaidSiege).
                var post = CompanionRaidSiege.FormationOverride(BotBody);
                var raidPoint = post ?? TemporaryGroupStableTravel.FormationPoint(leader, Math.Max(0, Body.GroupIndex - 1));
                if (post == null) raidPoint = CompanionFollowPolicy.FormationDestination(BotBody, raidPoint);
                _ambientWanderMovement = false;
                var point = new Point3D((int)raidPoint.X, (int)raidPoint.Y, (int)raidPoint.Z);
                if (Body.GetDistanceTo(point) > 35)
                {
                    CompanionFollowPolicy.BeginFormation(BotBody, raidPoint);
                    Body.PathTo(raidPoint, Body.MaxSpeed);
                }
                return;
            }
            AutonomousFormation.Offset formation = ambientWander
                ? AutonomousFormation.ForIdleWander(BotBody.Name, tightInterior, (int)(GameLoop.GameLoopTime / IDLE_WANDER_CYCLE_MS))
                : AutonomousFormation.For(BotBody.Name, tightInterior);
            double radians = formation.AngleDegrees * Math.PI / 180d;
            var destination = new Point3D(
                leader.X + (int)Math.Round(Math.Cos(radians) * formation.Distance),
                leader.Y + (int)Math.Round(Math.Sin(radians) * formation.Distance),
                leader.Z);
            if (companionTravel && !ambientWander && leader.CurrentZone != null)
            {
                Vector3 floor = CompanionFollowPolicy.FormationDestination(BotBody, new(destination.X, destination.Y, destination.Z));
                destination = new Point3D((int)floor.X, (int)floor.Y, (int)floor.Z);
            }
            int arrivalTolerance = ambientWander ? 12 : 20;
            if (Body.GetDistanceTo(destination) > arrivalTolerance)
            {
                if (companionTravel && !ambientWander)
                    CompanionFollowPolicy.BeginFormation(BotBody, new(destination.X, destination.Y, destination.Z));
                short speed = ambientWander
                    ? (short)Math.Clamp(Body.MaxSpeed * 2 / 5, 60, Body.MaxSpeed)
                    : Body.MaxSpeed;
                _ambientWanderMovement = ambientWander;
                if (companionTravel && !ambientWander)
                    Body.PathTo(destination, speed);
                else
                    Body.WalkTo(destination, speed);
            }
            else if (!Body.IsMoving)
                _ambientWanderMovement = false;
        }

        private void MaybeIdleRoleplay()
        {
            if (!CanAmbientWander || Body.IsMoving || Body.IsCasting || Body.InCombat)
                return;

            DateTime now = DateTime.UtcNow;
            if (!AutonomousBotChat.ShouldSpeak(now, _lastIdleRoleplayUtc, false))
                return;

            GameNPC nearbyMonster = Body.GetNPCsInRadius(1200)
                .Where(npc => npc != Body && npc.IsAlive && npc.Realm == eRealm.None && CanAggroTarget(npc))
                .OrderBy(Body.GetDistanceTo)
                .FirstOrDefault();
            var context = new AutonomousBotChat.Context(
                BotBody.Name,
                BotBody.ClassName,
                Body.CurrentZone?.Description,
                nearbyMonster?.Name,
                string.Empty,
                string.Empty,
                string.Empty,
                BotBody.Level,
                Math.Max(1, (int)(Body.Group?.MemberCount ?? 1)),
                BotBody.Realm);
            if (AutonomousBotChatCoordinator.TryStartAmbient(BotBody, context))
                _lastIdleRoleplayUtc = now;
        }


        private void OnOwnerAttacked(DOLEvent e, object sender, EventArgs args)
        {
            if (args is AttackedByEnemyEventArgs attackArgs)
                OnOwnerAttacked(attackArgs.AttackData);
        }

        public override void Think()
        {
            bool nativeSpeedTravelStarted;
            if (CompanionFollowPolicy.ObserveAndCancelBuffs(BotBody, out nativeSpeedTravelStarted))
            {
                _nextMaintenanceBuffTick = 0;
                _nextDeployablePetTick = 0;
            }
            // A parked companion may have just completed a no-missing-buffs
            // scan. On the first moving owner pulse, check native group speed
            // immediately instead of waiting for that idle-scan backoff.
            if (nativeSpeedTravelStarted)
                _nextMaintenanceBuffTick = 0;
            EnforceCompanionEngagementRange();
            GameLiving nearbyPull = CompanionEngagementMode.NearbyPull(BotBody);
            if (nearbyPull != null && CanAggroTarget(nearbyPull)) AssistPlayerAttack(nearbyPull);
            AlreadyCheckedHeals = false;
            if (!HasAggro && GameLoop.GameLoopTime >= _nextPoisonSupplyTick)
            {
                _nextPoisonSupplyTick = GameLoop.GameLoopTime + 2_000;
                BotPoisonSupply.Maintain(BotBody);
            }
            AutonomousSummonActivity.RecoverOwner(BotBody);
            BotAnimistPolicy.RestoreEncounterTarget(BotBody);
            ObserveLeaderActivity();
            BotBody?.WakeRecoveryRestIfCombatBlocked();
            BotBody?.EnsureAutonomousRecoveryTimers();

            if (AutonomousStuckWatchdog.Observe(BotBody))
                return;

            AutonomousBotRealmAbilities.UseActives(BotBody);

            // Once a stable ticket has boarded, its waypoint chain exclusively
            // owns movement until the final point. No ordinary bot subsystem is
            // allowed to issue a competing follow, cast, pet, or combat order.
            if (BotBody?.IsOnStableMasterRoute == true)
            {
                ThinkInterval = 1_500 + BotBody.ObjectID % 350;
                BotBody.MaintainStableMasterRoute();
                if (BotBody.IsMovingOnPath || BotBody.CurrentPathPoint != null)
                    return;
                BotBody.TryCompleteStableMasterRouteAfterArrival();
                return;
            }

            CompanionPvpEngagement.Observe(BotBody);
            // Goal 11: a /raid 40 or /raid 80 on a keep siege works its squad job (rams, engines) on its own.
            if (CompanionRaidSiege.Think(this))
                return;

            // Frontier enemies take priority over rally/follow/rest and optional
            // buffs, even on a PvE task. Horse travel above stays authoritative.
            if (BotBody?.IsAutonomousWorldBot == true && !BotBody.IsPlayerLedGroup)
            {
                _autonomousWorldController ??= new AutonomousWorldBotController();
                if (_autonomousWorldController.TryEngageFrontierThreat(this))
                {
                    ThinkInterval = AutonomousFidelityPolicy.IntervalMilliseconds(eAutonomousThinkMode.Combat,
                        CurrentFidelity(), AutonomousBotRegistry.PopulationForBrainTick);
                    FSM.Think();
                    return;
                }
            }

            if (AutonomousDefensivePull.Hold(BotBody))
            {
                if (!Body.IsCasting) CheckHeals();
                return;
            }

            // A Necromancer servant's long self-buff is cast by the zombie,
            // not the shade. Hold the shade's already-issued route while that
            // native servant command is queued/casting so tether-follow cannot
            // drag the zombie out of its cast. This is deliberately below
            // stable travel and combat-aware: damage or a live combat order
            // releases it immediately, and the unchanged goal controller
            // resumes the route as soon as the buff completes.
            if (TryHoldForNecromancerServantSelfBuff())
                return;

            ClearIdleOwnedPetTarget();

            if (BotBody?.IsTemporaryGroupHelper == true &&
                (BotBody.TryReturnTemporaryCompanionToLeader() || TryResurrectCompanionOwner()))
                return;

            if (PlayerLedPullCoordinator.IsWaiting(BotBody))
            {
                // The tank alone owns the approach. Waiting allies may heal or
                // buff, but cannot chase or send a pet ahead of first contact.
                // A companion that was recovering before the pull must stand
                // immediately; it must never retain the sitting pose in combat.
                if (BotBody.IsRecoveryResting)
                    BotBody.WakeTemporaryCompanionRest();
                if (!CheckHeals()) CheckSpells(eCheckSpellType.Defensive);
                return;
            }

            GameLiving leaderTarget = PlayerLedPullCoordinator.FindLeaderTarget(AssistedPlayer);
            if (leaderTarget != null && PlayerLedPullCoordinator.Available(BotBody, AssistedPlayer) &&
                Body.IsWithinRadius(leaderTarget, GROUP_DEFENSE_ASSIST_RADIUS))
                AssistPlayerAttack(leaderTarget);

            if (BotBody?.TryApplyEndgameCompanionUpgrade() == true)
                return;

            WakeNearbyNaturalAggroBrains();

            // Narrow failsafe for an ephemeral /spawn companion whose owner's
            // completed portal/region transfer was not observed by its event.
            // Persistent bots and ordinary player group members never enter it.
            if (TemporaryGroupStableTravel.EnsureOwnerTransferCohesion(BotBody))
                return;

            // A used-up self bladeturn goes back up as soon as nothing is fighting
            // the bot, before rest and the rest of the upkeep (gamebots and companions).
            if (TryRestoreSelfBladeturn())
                return;

            if (TryMaintainTemporaryCompanionBonedancerArmy())
                return;

            // A parked /spawn party rests before optional songs, chants, buffs,
            // pet upkeep, or ambient roleplay can claim the AI turn. Combat,
            // pull orders, owner movement, resurrection/return travel, and full
            // recovery all release the lock and continue through normal AI.
            // Persistent autonomous playerbots never enter this branch.
            if (TryMaintainTemporaryCompanionRest())
                return;

            eAutonomousThinkMode mode = CurrentThinkMode();
            eAutonomousFidelity fidelity = CurrentFidelity();
            if (BotBody != null)
                BotBody.AutonomousFidelity = fidelity;
            // Set the cadence before any autonomous handler can return. Movement
            // remains continuous in the movement service between decisions.
            int interval = AutonomousFidelityPolicy.IntervalMilliseconds(mode, fidelity, AutonomousBotRegistry.PopulationForBrainTick);
            ThinkInterval = mode == eAutonomousThinkMode.Combat
                ? interval
                : AutonomousFidelityPolicy.Stagger(interval, BotBody?.DatabaseID ?? Body?.ObjectID ?? 0);

            // The forty-five minute goal watchdog is about a bot that has made
            // neither combat nor economic/XP progress. Record active fighting
            // at a coarse cadence so movement alone cannot hide a dead goal and
            // combat itself does not create per-frame persistence traffic.
            if (mode == eAutonomousThinkMode.Combat && BotBody?.IsAutonomousWorldBot == true &&
                (BotBody.InCombatInLast(15_000) || BotBody.ControlledBrain?.Body?.InCombatInLast(15_000) == true) &&
                GameLoop.GameLoopTime >= _nextCombatProgressTick)
            {
                _nextCombatProgressTick = GameLoop.GameLoopTime + 15_000 + BotBody.ObjectID % 2_000;
                AutonomousStuckWatchdog.MarkProgress(BotBody, eAutonomousProgressKind.Combat);
            }

            // Classic song classes rotate only learned, level-valid chants.
            // Every pulse is started through the ordinary spell handler and
            // therefore keeps its real instrument, power and interruption rules.
            // Grouped performers favor this support job; solo levelers never let
            // twisting displace their own immediate combat response.
            // A parked /spawn performer completes its ordinary buff pass before
            // another song can consume the cast slot. The batch flag below
            // keeps the song paused across the normal 1.75s buff backoff; a
            // moving owner releases it immediately for travel songs instead.
            bool companionPerformerBuffPriority = IsStationaryTemporaryCompanionPerformer();
            if (companionPerformerBuffPriority &&
                !BotRestRecovery.DeferOptionalCasterUpkeep(BotBody) &&
                TryMaintainTravelAndClassBuffs())
                return;

            FollowTravelingCompanionPerformer();
            if (TryMaintainClassicSongTwist())
                return;

            // Instant tank chants must not consume the attack/follow turn.
            TryMaintainTankChant();

            // A failed level-one summon/buff must never starve the world goal
            // controller.  The affected primary caster-pet classes postpone
            // upkeep while acquiring or following a route, then resume normal
            // pet/buff behavior at the destination or as soon as combat starts.
            bool movementFirstPetClass = BotBody != null &&
                AutonomousPetSupport.ShouldPrioritizeWorldMovement(
                    (eCharacterClass)BotBody.CharacterClass.ID,
                    BotBody.IsAutonomousWorldBot,
                    BotBody.IsTemporaryGroupHelper,
                    BotBody.IsPlayerLedGroup,
                    mode == eAutonomousThinkMode.Combat,
                    BotBody.IsMoving,
                    !string.IsNullOrWhiteSpace(BotBody.PersistentRecord?.CurrentCampId),
                    BotBody.PersistentRecord?.Activity);
            bool requiredBonedancerArmyUpkeep =
                AutonomousPetSupport.NeedsBonedancerArmyUpkeep(BotBody);
            bool requiredPrimaryPetUpkeep =
                AutonomousPetSupport.NeedsPrimaryPetUpkeep(BotBody);

            // A dungeon corridor pull is already committed after rest/readiness
            // checks. Do not let optional travel buffs repeatedly consume its
            // first combat turn. Missing primary pets still take their normal
            // upkeep path, and the combat FSM retains healing/casting rules.
            if (BotBody?.IsAutonomousWorldBot == true && !BotBody.IsPlayerLedGroup &&
                Body.CurrentZone?.IsDungeon == true && HasCommittedDungeonPull && !Body.InCombat &&
                !requiredPrimaryPetUpkeep && !requiredBonedancerArmyUpkeep &&
                Body.TargetObject is GameLiving { IsAlive: true } dungeonPull)
            {
                if (dungeonPull.CurrentRegion == Body.CurrentRegion &&
                    FSM.GetCurrentState()?.StateType == eFSMStateType.AGGRO)
                {
                    FSM.Think();
                    return;
                }
            }

            // Persistent world bots and temporary /spawn companions share this
            // conservative maintenance pass. It uses only learned spells and
            // real equipment, and never refreshes routine buffs in combat.
            bool deferCasterUpkeep = BotRestRecovery.DeferOptionalCasterUpkeep(BotBody);
            if (!companionPerformerBuffPriority && !movementFirstPetClass && !deferCasterUpkeep &&
                TryMaintainTravelAndClassBuffs())
                return;

            // Adopt a live target from our own pet before optional pet upkeep.
            // Previously this ran after Maintain(), so a pet heal/buff/summon
            // could consume every owner turn while a Hunter's pet fought alone.
            GameLiving ownedPetTarget = BotBody?.IsAutonomousWorldBot == true && !BotBody.IsPlayerLedGroup
                ? AutonomousPetSupport.ActiveOwnedPetCombatTarget(BotBody)
                : null;
            if (ownedPetTarget != null && CanDefendAgainst(ownedPetTarget))
            {
                AddToAggroList(ownedPetTarget, Math.Max(1, ownedPetTarget.EffectiveLevel));
                EnterOwnedPetCombatState(FSM);
            }

            GameLiving orderedPullTarget = ActiveOrderedPullTarget;
            // While a caster walks into spell range its pet waits with it,
            // unless the human leader (or the leader's pet) is already fighting
            // that target: then the pet joins the fight immediately.
            bool closingOnPullTarget = orderedPullTarget != null &&
                                       PlayerLedCasterEngagementRange(orderedPullTarget) is int range && range > 0 &&
                                       !Body.IsWithinRadius(orderedPullTarget, range) &&
                                       !LeaderIsFighting(orderedPullTarget);
            GameLiving petCombatTarget = UsesDefensiveOnlyPet || closingOnPullTarget
                ? null
                : ResolvePetCombatTarget(orderedPullTarget ?? Body?.TargetObject as GameLiving ?? ownedPetTarget);
            bool archerCombatOwnsPetUpkeep = BotBody?.CharacterClass != null &&
                BotRangedCombat.CombatOwnsArcherPetUpkeep(
                    (eCharacterClass)BotBody.CharacterClass.ID,
                    petCombatTarget?.IsAlive == true,
                    Body.InCombat,
                    Body.IsAttacking,
                    Body.attackComponent?.AttackState == true &&
                    Body.ActiveWeaponSlot == eActiveWeaponSlot.Distance);
            bool requiredPetUpkeep = requiredPrimaryPetUpkeep || requiredBonedancerArmyUpkeep;
            // A missing primary pet (or a missing Bonedancer subordinate) is
            // combat equipment, not an optional buff. Always allow that legal
            // summon attempt. The archer guard suppresses only routine upkeep
            // on an existing pet, so stale targets can never leave a Hunter
            // permanently petless while traveling.
            if ((!archerCombatOwnsPetUpkeep || requiredPetUpkeep) &&
                (BotAnimistPolicy.AppliesTo(BotBody) || requiredPetUpkeep ||
                 (!movementFirstPetClass && !deferCasterUpkeep)) &&
                AutonomousPetSupport.Maintain(Body, petCombatTarget, ref _nextDeployablePetTick, out _))
                return;

            // A controlled pet may reach the camp first while its persistent
            // owner loses a stale/expired aggro entry. Re-adopt only the live
            // target that this bot's own pet tree is already fighting. The
            // ordinary ranged-caster or bow policy below then closes to its
            // proper attack range; it never turns a caster into a melee bot.
            // Player-led persistent bots still publish bot-only group metadata
            // for the launcher. The human leader is never serialized there.
            if (BotBody?.IsAutonomousWorldBot == true && BotBody.IsPlayerLedGroup)
                AutonomousBotGroupCoordinator.Pulse(BotBody);

            MaybeAutonomousWorldChat();

            if (TryHandleAutonomousMerchantService())
                return;

            if (TryHandleAutonomousRealmExchange())
                return;

            if (BotBody?.IsAutonomousWorldBot == true && !BotBody.IsPlayerLedGroup)
            {
                _autonomousWorldController ??= new AutonomousWorldBotController();
                if (_autonomousWorldController.Tick(this))
                {
                    // A decision that just started a fight (a route or camp pull) thinks again at the
                    // combat cadence. The travel/planning interval chosen above (up to ~10 s for a
                    // standing bot far from players) outlasted the aggro state's 6 s first-contact
                    // window, so the pull expired unthrown and was re-issued forever: 1,479 such
                    // loops (186 bot-hours) in run 9, 2026-10-06.
                    if (mode != eAutonomousThinkMode.Combat && HasAggro)
                        ThinkInterval = AutonomousFidelityPolicy.IntervalMilliseconds(eAutonomousThinkMode.Combat,
                            fidelity, AutonomousBotRegistry.PopulationForBrainTick);
                    return;
                }
            }

            FSM.Think();
        }

        private bool TryHoldForNecromancerServantSelfBuff()
        {
            GameBot owner = BotBody;
            if (owner?.ControlledBrain is not NecromancerPetBrain servantBrain ||
                servantBrain.Body?.IsAlive != true || !servantBrain.HasPendingSelfBuffCommand ||
                ActiveOrderedPullTarget?.IsAlive == true ||
                BotRestRecovery.BlocksRest(owner))
            {
                return false;
            }

            if (owner.IsMoving)
                owner.StopMoving();
            if (servantBrain.Body.IsMoving)
                servantBrain.Body.StopMoving();
            return true;
        }

        public static void EnterOwnedPetCombatState(FSM fsm)
        {
            // FSM.SetCurrentState always runs Exit, even for the same state.
            // Aggro.Exit stops the pending bow/cast attack and clears aggro.
            // Re-adopting our pet's opponent is not a new combat transition.
            // Keep this local to bot pet adoption; other FSM users retain their
            // intentional exit/re-entry semantics.
            if (fsm.GetCurrentState() != fsm.GetState(eFSMStateType.AGGRO))
                fsm.SetCurrentState(eFSMStateType.AGGRO);
        }

        private void ClearIdleOwnedPetTarget()
        {
            if (Body?.TargetObject == null || Body.InCombat || Body.IsAttacking || Body.IsCasting || HasAggro ||
                Body.ControlledBrain?.Body?.InCombat == true)
                return;

            if (Body.TargetObject == Body || IsOwnedPetTreeTarget(Body.ControlledBrain, Body.TargetObject, 0))
                Body.TargetObject = null;
        }

        private static bool IsOwnedPetTreeTarget(IControlledBrain brain, GameObject target, int depth)
        {
            if (brain?.Body == null || depth > 3)
                return false;
            if (brain.Body == target)
                return true;

            IControlledBrain[] children = brain.Body.ControlledNpcList;
            if (children == null)
                return false;
            foreach (IControlledBrain child in children)
                if (IsOwnedPetTreeTarget(child, target, depth + 1))
                    return true;
            return false;
        }

        /// <summary>
        /// Real clients wake APlayerVicinityBrain mobs through object-create
        /// packets. GameBots have no client, so they explicitly keep only nearby
        /// hostile brains awake. The mob still discovers and attacks the bot via
        /// StandardMobBrain.CheckNpcAggro using its database-authored range,
        /// con/faction checks, LoS, leash and combat FSM.
        /// </summary>
        private void WakeNearbyNaturalAggroBrains()
        {
            GameBot bot = BotBody;
            long now = GameLoop.GameLoopTime;
            if (bot == null || now < _nextNaturalMobWakeTick)
                return;

            _nextNaturalMobWakeTick = now + 1_250 + bot.ObjectID % 500;
            if (!bot.IsAlive || bot.ObjectState != GameObject.eObjectState.Active ||
                bot.IsOnStableMasterRoute || bot.IsStealthed ||
                bot.effectListComponent.ContainsEffectForEffectType(eEffect.Shade))
                return;

            foreach (GameNPC npc in bot.GetNPCsInRadius(StandardMobBrain.MAX_AGGRO_DISTANCE))
            {
                if (npc == bot || !npc.IsAlive || npc.ObjectState != GameObject.eObjectState.Active ||
                    npc is GameTaxi or GameTrainingDummy || npc.Brain is not StandardMobBrain brain ||
                    brain.AggroLevel <= 0 || brain.AggroRange <= 0 ||
                    !npc.IsWithinRadius(bot, brain.AggroRange) || !brain.CanAggroTarget(bot))
                    continue;

                npc.OnAutonomousBotNearby();
            }
        }

        private void MaybeAutonomousWorldChat()
        {
            GameBot bot = BotBody;
            long nowTick = GameLoop.GameLoopTime;
            if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup ||
                nowTick < _nextAmbientChatCheckTick)
                return;
            _nextAmbientChatCheckTick = nowTick + 15_000 + Random.Shared.Next(20_001) + bot.ObjectID % 4_000;

            DateTime now = DateTime.UtcNow;
            if (!AutonomousBotChat.ShouldSpeak(now, _lastIdleRoleplayUtc,
                    bot.InCombat || bot.IsAttacking || bot.IsCasting || bot.IsOnStableMasterRoute))
                return;

            OfflineWorldBotRecord record = bot.PersistentRecord;
            string item = bot.Inventory?.AllItems
                .FirstOrDefault(entry => entry != null && entry.SlotPosition >= (int)eInventorySlot.FirstBackpack &&
                                         entry.SlotPosition <= (int)eInventorySlot.LastBackpack)?.Name ?? string.Empty;
            var context = new AutonomousBotChat.Context(
                bot.Name, bot.ClassName, bot.CurrentZone?.Description,
                record?.TargetName, record?.TravelDestination, item, string.Empty,
                bot.Level, Math.Max(1, (int)(bot.Group?.MemberCount ?? 1)), bot.Realm);
            if (AutonomousBotChatCoordinator.TryStartAmbient(bot, context))
                _lastIdleRoleplayUtc = now;
        }

        private string _lastBetweenTaskPurchaseAssignment = string.Empty; // Ordinary vendor purchase throttle only.

        private long _nextArrivedServiceWarning;
        internal bool TryUseArrivedAutonomousService(GameNPC service)
        {
            if (service?.ObjectState != GameObject.eObjectState.Active || BotBody == null ||
                !BotBody.IsWithinRadius(service, GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE)) return false;
            // The world controller already selected a reachable merchant and stopped
            // beside it. Use that exact merchant on this turn; another AI action can
            // otherwise postpone the ordinary merchant pulse indefinitely.
            if (service is GameMerchant merchant && service is not RealmExchangeBroker)
                return TryHandleAutonomousMerchantService(merchant);
            if (service is not RealmExchangeBroker broker) return false;
            bool handled = TryHandleAutonomousRealmExchange(broker);
            if (handled)
            {
                _nextArrivedServiceWarning = GameLoop.GameLoopTime + 60_000;
                return true;
            }
            if (_nextArrivedServiceWarning == 0)
                _nextArrivedServiceWarning = GameLoop.GameLoopTime + 60_000;
            if (GameLoop.GameLoopTime >= _nextArrivedServiceWarning)
            {
                _nextArrivedServiceWarning = GameLoop.GameLoopTime + 60_000;
                log.Warn($"AUTONOMOUS_SERVICE_BLOCKED bot=\"{BotBody.Name}\" id={BotBody.DatabaseID} service=\"{broker.Name}\" " +
                    $"alive={BotBody.IsAlive} grouped={BotBody.Group != null} combat={BotBody.InCombat} " +
                    $"attacking={BotBody.IsAttacking} casting={BotBody.IsCasting} moving={BotBody.IsMoving} " +
                    $"capital={BotBody.CurrentRegion?.IsCapitalCity} inventoryPhase={AutonomousObjectiveAssignments.WantsBetweenTaskInventory(BotBody)} " +
                    $"expired={AutonomousObjectiveAssignments.BetweenTaskServiceExpired(BotBody)} cooldown={GameLoop.GameLoopTime < _nextExchangeCheckTick}");
            }
            return false;
        }

        private bool TryHandleAutonomousRealmExchange(RealmExchangeBroker arrivedBroker = null)
        {
            GameBot bot = BotBody;
            long now = GameLoop.GameLoopTime;
            if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup ||
                bot.Group != null || !bot.IsAlive || bot.InCombat || bot.IsAttacking || bot.IsCasting ||
                bot.IsMoving || bot.CurrentRegion?.IsCapitalCity != true ||
                !AutonomousObjectiveAssignments.WantsBetweenTaskInventory(bot) ||
                AutonomousObjectiveAssignments.BetweenTaskServiceExpired(bot))
            {
                _pendingExchangeItem = null;
                _pendingExchangePrice = 0;
                _pendingExchangeListTick = 0;
                return false;
            }

            RealmExchangeBroker broker = arrivedBroker ?? bot.GetNPCsInRadius((ushort)Math.Clamp(GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE, 1, ushort.MaxValue))
                .OfType<RealmExchangeBroker>().FirstOrDefault(candidate => candidate.Realm == bot.Realm);
            if (broker == null || broker.Realm != bot.Realm ||
                !bot.IsWithinRadius(broker, GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE))
                return false;

            if (_pendingExchangeItem != null)
            {
                if (now < _pendingExchangeListTick)
                {
                    bot.StopMoving();
                    return true;
                }

                DbInventoryItem item = _pendingExchangeItem;
                int price = _pendingExchangePrice;
                _pendingExchangeItem = null;
                _pendingExchangePrice = 0;
                _pendingExchangeListTick = 0;
                bool listed = AutonomousBotEconomy.TryList(bot, item, price);
                SetExchangeStatus(bot, listed
                    ? $"Listed {item.Name} on the Realm Exchange"
                    : $"Kept {item.Name} after its listing failed",
                    listed ? $"Real item listed for {price} copper" : "The real inventory item was not removed");
                if (listed)
                    AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                _nextExchangeCheckTick = now + 3_000 + Random.Shared.Next(4_001);
                return listed;
            }

            if (now < _nextExchangeCheckTick)
                return false;
            _nextExchangeCheckTick = now + 3_000 + Random.Shared.Next(4_001);

            // Reserve and post the single best item before shopping, so a full
            // 40-slot backpack—not a partially vendored remainder—determines
            // the listing. The world controller routes to a vendor after this
            // slot is occupied and returns here for upgrades only after clearing.
            AutonomousBotEconomy.ListingCandidate candidate = AutonomousBotEconomy.FindValuableListingCandidate(bot);
            if (candidate != null &&
                AutonomousBotEconomy.FindFreeListingSlot(AutonomousBotEconomy.GetOwnerId(bot.DatabaseID)) >= 0)
            {
                int listingPrice = AutonomousBotEconomy.RollListingPrice(candidate.Item);

                bool advertised = Random.Shared.NextDouble() < 0.70 &&
                                  AutonomousBotChatCoordinator.TryAdvertiseExchangeItem(bot, candidate.Item.Name);
                if (!advertised)
                {
                    bool listed = AutonomousBotEconomy.TryList(bot, candidate.Item, listingPrice);
                    if (listed)
                    {
                        SetExchangeStatus(bot, $"Listed {candidate.Item.Name} on the Realm Exchange",
                            $"Real item listed for {listingPrice} copper");
                        AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                    }
                    return listed;
                }

                _pendingExchangeItem = candidate.Item;
                _pendingExchangePrice = listingPrice;
                _pendingExchangeListTick = now + 7_000 + Random.Shared.Next(4_001);
                SetExchangeStatus(bot, $"Asking the realm about {candidate.Item.Name}",
                    $"Will list the real item for {listingPrice} copper after the chatter");
                bot.StopMoving();
                return true;
            }

            AutonomousBotEconomy.PurchaseCandidate purchase = AutonomousBotEconomy.IsBackpackFull(bot)
                ? null : AutonomousBotEconomy.FindBestUsefulExchangePurchase(bot);
            if (purchase != null)
            {
                int paidCopper = purchase.Item.SellPrice;
                if (AutonomousBotEconomy.TryBuy(bot, purchase.Item))
                {
                    bool equipped = AutonomousBotEconomy.TryEquipPurchasedUpgrade(bot, purchase);
                    SetExchangeStatus(bot,
                        equipped ? $"Bought and equipped {purchase.Item.Name}" : $"Bought {purchase.Item.Name} from the Realm Exchange",
                        $"Paid {paidCopper} copper from real saved coin");
                    AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Money);
                    AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                    _nextExchangeCheckTick = now + 3_000 + Random.Shared.Next(4_001);
                    return true;
                }
            }

            // Another buyer can remove the prospective upgrade while this bot
            // travels here. Do not keep routing to a stale cached service for
            // another five minutes when the actual broker has nothing to do.
            AutonomousBotEconomy.MarkEconomyChanged(bot.DatabaseID);
            return false;
        }

        /// <summary>
        /// Executes only after an autonomous persistent bot has physically reached an ordinary
        /// merchant.  Routing remains the world-controller's responsibility; this method has no
        /// movement side effects, cannot affect /spawn helpers, and uses only saved bot inventory
        /// and copper through AutonomousBotEconomy.
        /// </summary>
        private bool TryHandleAutonomousMerchantService(GameMerchant arrivedMerchant = null)
        {
            GameBot bot = BotBody;
            long now = GameLoop.GameLoopTime;
            if (bot?.IsAutonomousWorldBot != true || bot.IsTemporaryGroupHelper || bot.IsPlayerLedGroup ||
                bot.Group != null || !bot.IsAlive || bot.InCombat || bot.IsAttacking || bot.IsCasting || bot.IsMoving ||
                now < _nextMerchantServiceTick || !AutonomousObjectiveAssignments.WantsBetweenTaskInventory(bot) ||
                AutonomousObjectiveAssignments.BetweenTaskServiceExpired(bot))
                return false;

            ushort interactionRadius = (ushort)Math.Clamp(
                GS.ServerProperties.Properties.WORLD_PICKUP_DISTANCE, 1, ushort.MaxValue);
            // Capital service areas contain several merchants inside the old
            // 900-unit lookup ring. FirstOrDefault could therefore select a
            // different, out-of-range merchant while the world controller had
            // correctly parked the bot beside its chosen vendor. The sale then
            // failed forever and the watchdog repeatedly recovered the bot.
            // Consider only merchants the bot can actually interact with and
            // prefer the nearest one; the normal sale/range checks remain the
            // final authority.
            GameMerchant merchant = arrivedMerchant;
            if (merchant == null)
                merchant = bot.GetNPCsInRadius(interactionRadius)
                    .OfType<GameMerchant>()
                    .Where(candidate => candidate.IsWithinRadius(bot, interactionRadius) &&
                                        (candidate.Realm == eRealm.None || candidate.Realm == bot.Realm))
                    .OrderBy(candidate => bot.GetDistanceTo(candidate))
                    .FirstOrDefault();
            if (merchant?.ObjectState != GameObject.eObjectState.Active ||
                !merchant.IsWithinRadius(bot, interactionRadius) ||
                merchant.Realm != eRealm.None && merchant.Realm != bot.Realm)
                return false;

            if (bot.Inventory?.AllItems.Any(i=>BotSiegeRuntime.IsSupply(i.Id_nb))==true)
            {
                int repairKits=BotSiegeRuntime.TryRestockRepairKits(bot,merchant);
                if (repairKits>0)
                {
                    SetMerchantStatus(bot,$"Restocked {repairKits} siege repair kits", "Paid from saved coins; topped up to five at the current merchant");
                    AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                    _nextMerchantServiceTick=now+30_000;
                    return true;
                }
            }

            eWorldServiceKind? service = AutonomousBotEconomy.GetNeededService(bot);
            int usedSlots = bot.Inventory?.CountSlots(true, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack) ?? 0;
            int capacity = (int)eInventorySlot.LastBackpack - (int)eInventorySlot.FirstBackpack + 1;
            if (service == eWorldServiceKind.Vendor || usedSlots >= capacity)
            {
                DbInventoryItem trash = AutonomousBotEconomy.FindVendorTrashCandidate(bot);
                if (trash != null && AutonomousBotEconomy.TrySellToVendor(bot, merchant, trash, out long earned))
                {
                    SetMerchantStatus(bot, $"Sold {trash.Name} to {merchant.Name}", $"Received {earned} saved copper from a real vendor");
                    AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Money);
                    AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                    _nextMerchantServiceTick = now + 3_000;
                    return true;
                }
            }

            // Legacy full bags can contain a still-useful equipment upgrade.
            // Wear it first, without discarding or vending it; the displaced
            // piece becomes an ordinary backpack candidate on the next pulse.
            if (usedSlots >= capacity &&
                AutonomousBotEconomy.TryEquipBestOwnedUpgrade(bot, out DbInventoryItem equippedUpgrade))
            {
                SetMerchantStatus(bot, $"Equipped {equippedUpgrade.Name} before clearing the backpack",
                    "The replaced equipment remains in the real inventory for appraisal");
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Inventory);
                AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                _nextMerchantServiceTick = now + 3_000;
                return true;
            }

            // An old character may carry 40 non-vendorable quest/reputation
            // objects indefinitely. Clear expendable backpack items only
            // after it reaches a merchant and no ordinary sale exists.
            // Equipped and swappable gear, instruments and money stay intact.
            if (usedSlots >= capacity &&
                AutonomousBotEconomy.TryClearBlockedBackpackAtMerchant(bot, true, out int discarded))
            {
                SetMerchantStatus(bot, $"Cleared {discarded} blocked backpack items at {merchant.Name}",
                    "No backpack item could be sold; combat gear, instruments and saved copper were kept");
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Inventory);
                AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                _nextMerchantServiceTick = now + 30_000;
                return true;
            }

            if (_lastBetweenTaskPurchaseAssignment != bot.PersistentRecord.ObjectiveAssignmentId &&
                AutonomousBotEconomy.TryBuyUsefulVendorUpgrade(bot, merchant, out DbInventoryItem purchased, out long spent))
            {
                _lastBetweenTaskPurchaseAssignment = bot.PersistentRecord.ObjectiveAssignmentId;
                SetMerchantStatus(bot, $"Bought {purchased.Name} from {merchant.Name}", $"Spent {spent} saved copper on a legal equipment upgrade");
                AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Money);
                AutonomousObjectiveAssignments.TryCompleteBetweenTaskServicesIfSatisfied(bot);
                _nextMerchantServiceTick = now + 30_000 + Random.Shared.Next(30_001);
                return true;
            }

            _nextMerchantServiceTick = now + 20_000 + Random.Shared.Next(20_001);
            AutonomousBotEconomy.MarkEconomyChanged(bot.DatabaseID);
            return false;
        }

        private static void SetMerchantStatus(GameBot bot, string activity, string progress)
        {
            if (bot?.PersistentRecord == null)
                return;
            bot.PersistentRecord.Activity = activity;
            bot.PersistentRecord.CurrentGoal = "Use a real merchant with finite saved inventory and copper";
            bot.PersistentRecord.ObjectiveProgress = progress;
            bot.PersistentRecord.TargetName = string.Empty;
            bot.PersistentRecord.TravelDestination = bot.CurrentZone?.Description ?? string.Empty;
            bot.MarkAutonomousStateDirty();
        }

        private static void SetExchangeStatus(GameBot bot, string activity, string progress)
        {
            if (bot?.PersistentRecord == null)
                return;
            bot.PersistentRecord.Activity = activity;
            bot.PersistentRecord.CurrentGoal = "Use the Realm Exchange with real inventory and coin";
            bot.PersistentRecord.ObjectiveProgress = progress;
            bot.PersistentRecord.TargetName = string.Empty;
            bot.PersistentRecord.TravelDestination = bot.CurrentZone?.Description ?? string.Empty;
            bot.MarkAutonomousStateDirty();
            AutonomousStuckWatchdog.MarkProgress(bot, eAutonomousProgressKind.Objective);
        }

        private bool TryMaintainTravelAndClassBuffs()
        {
            GameBot bot = BotBody;
            bool waitingForLeader = CompanionFollowPolicy.WaitingForLeaderToStop(bot);
            if (waitingForLeader && !CompanionFollowPolicy.CanStopForNativeTravelSpeed(bot))
                return false;
            if (bot == null || !bot.IsAlive || bot.IsOnStableMasterRoute || bot.InCombat || HasAggro ||
                bot.IsAttacking || bot.IsCasting || bot.IsStunned || bot.IsMezzed || bot.IsSilenced ||
                bot.castingComponent?.HasPendingSkillRequests == true ||
                GameLoop.GameLoopTime < _nextMaintenanceBuffTick)
                return false;

            bool autonomousPetUpkeep = bot.CharacterClass != null &&
                AutonomousPetSupport.OwnsPetUpkeep((eCharacterClass)bot.CharacterClass.ID);
            List<Spell> known = (bot.MiscSpells ?? [])
                .Concat(bot.InstantMiscSpells ?? [])
                .Where(spell => spell != null && !spell.IsHarmful && spell.Level <= bot.Level &&
                                IsMaintainableClassBuff(spell) && !IsShortCombatShield(spell) &&
                                (!autonomousPetUpkeep ||
                                 spell.Target is not (eSpellTarget.PET or eSpellTarget.CONTROLLED)) &&
                                (spell.Target is not (eSpellTarget.PET or eSpellTarget.CONTROLLED) ||
                                 AutonomousPetSupport.ShouldMaintainRoutinePetBuff(spell, false)) &&
                                (!waitingForLeader || CompanionFollowPolicy.CanStopForNativeTravelSpeed(bot, spell)) &&
                                !BotSongTwistPolicy.IsReservedPulse(bot, spell))
                .DistinctBy(spell => spell.ID)
                .ToList();
            if (known.Count == 0)
            {
                CompleteCompanionPerformerBuffBatch(bot);
                return false;
            }

            bool traveling = IsMaintenanceTraveling();

            // Native helpful pulses replace one another. Recasting every
            // missing child alternated caster speed and bladeturn indefinitely,
            // stopping Runemasters/Theurgists on each cast before route work.
            // Managed performer/tank chants were already excluded above.
            int maintenancePulse = BotMaintenancePulsePolicy.Choose(known, traveling);
            IEnumerable<Spell> candidates = known
                .Where(spell => BotMaintenancePulsePolicy.CanMaintain(spell, maintenancePulse))
                .Where(spell => spell.SpellType != eSpellType.SpeedEnhancement || traveling)
                .OrderByDescending(spell => spell.SpellType == eSpellType.SpeedEnhancement && traveling)
                .ThenByDescending(spell => spell.Value)
                .ThenByDescending(spell => spell.Level);

            GameLiving outOfRangeBuffTarget = null;
            foreach (Spell spell in candidates)
            {
                if (spell.HasRecastDelay && bot.GetSkillDisabledDuration(spell) > 0)
                    continue;
                if (bot.Mana < bot.PowerCost(spell))
                    continue;
                if (!bot.CanAffordConcentration(spell))
                    continue;
                if (spell.NeedInstrument && !TryEquipRealInstrument(bot, spell.InstrumentRequirement))
                    continue;

                GameLiving target = FindMissingMaintenanceTarget(spell);
                if (target == null)
                {
                    outOfRangeBuffTarget ??= FindOutOfRangeBuffTarget(spell);
                    continue;
                }
                // A pet buff just cast whose effect has not shown up yet keeps its
                // old bounded retry window; other missing buffs may go next.
                if (target == bot.ControlledBrain?.Body && spell.ID == _pendingMaintenancePetBuffId &&
                    GameLoop.GameLoopTime < _pendingMaintenancePetBuffUntil)
                    continue;

                bool companionPerformerBuff = IsStationaryTemporaryCompanionPerformer();
                if (companionPerformerBuff)
                    StopTwistedSong();
                GameObject previousTarget = bot.TargetObject;
                if (spell.CastTime > 0)
                {
                    bot.StopMovingOnPath();
                    bot.StopMoving();
                }
                bot.TargetObject = target;
                bool cast = CastCoordinatedBuff(spell, false);
                bot.TargetObject = previousTarget;
                if (cast && companionPerformerBuff)
                    _companionPerformerBuffBatch = true;
                bool isMainPetTarget = target == bot.ControlledBrain?.Body;
                // Pet effects can be applied asynchronously. The just-cast pet buff
                // is skipped for 15 seconds unless its effect appears (the
                // Cabalist/Enchanter pile symptom), while the next missing buff
                // follows right after the cast instead of waiting 15 seconds.
                if (cast && isMainPetTarget)
                {
                    _pendingMaintenancePetBuffId = spell.ID;
                    _pendingMaintenancePetBuffUntil = GameLoop.GameLoopTime + 15_000;
                }
                _nextMaintenanceBuffTick = GameLoop.GameLoopTime +
                    (cast ? (isMainPetTarget ? Math.Max(1_750, spell.CastTime + 500) : 1_750) : 5_000);

                // Persistent world bots must keep their route/goal scheduler
                // alive after a non-combat pet upkeep cast. The native cast
                // pipeline remains authoritative; returning false here merely
                // lets the controller run its normal movement decision for the
                // same pulse. Player-led and temporary companions retain the
                // ordinary action-priority behavior.
                if (cast && isMainPetTarget && bot.IsAutonomousWorldBot &&
                    !bot.IsPlayerLedGroup && !bot.InCombat && !HasAggro)
                    return false;

                return cast;
            }

            // Nothing castable from here, but a group member out of range still needs
            // a buff: walk toward them (not while following a moving leader).
            if (outOfRangeBuffTarget != null && !waitingForLeader && ApproachSupportTarget(outOfRangeBuffTarget))
            {
                _nextMaintenanceBuffTick = GameLoop.GameLoopTime + 1_000;
                return true;
            }

            // A complete scan found nothing missing. Avoid rescanning the whole
            // spellbook on every lightweight AI tick.
            _nextMaintenanceBuffTick = GameLoop.GameLoopTime + 8_000;
            CompleteCompanionPerformerBuffBatch(bot);
            return false;
        }

        private int _pendingMaintenancePetBuffId;
        private long _pendingMaintenancePetBuffUntil;
        private long _nextSelfBladeturnTick;

        /// <summary>The self "absorb the next hit" shields: Protecting Spirit, Barrier of Warding, Barrier of Negation and the like.</summary>
        public static bool IsSelfBladeturn(Spell spell) =>
            spell?.SpellType == eSpellType.Bladeturn && spell.Target == eSpellTarget.SELF &&
            !spell.IsPulsing && !spell.IsHarmful;

        /// <summary>Safe to stop for a cast: nothing on the aggro list, not swinging, not busy, not hit in the last 3 seconds.</summary>
        public static bool SelfBladeturnSafe(bool alive, bool hasAggro, bool attacking, bool busy,
            bool crowdControlled, long sinceAttackedMilliseconds) =>
            alive && !hasAggro && !attacking && !busy && !crowdControlled && sinceAttackedMilliseconds >= 3_000;

        /// <summary>
        /// A used-up self bladeturn is recast as soon as it is safe. It used to go
        /// through the general buff upkeep, which waits for the combat flag to
        /// clear, skips offensive casters while they rest, and puts the shield
        /// behind every other long buff, so it came back long after the fight.
        /// </summary>
        private bool TryRestoreSelfBladeturn()
        {
            GameBot bot = BotBody;
            if (bot == null || !bot.IsAlive)
                return false;
            long now = GameLoop.GameLoopTime;
            // Hold the turn while our own shield cast finishes, so a route or
            // follow order cannot cancel it.
            if (bot.IsCasting && IsSelfBladeturn(bot.castingComponent?.SpellHandler?.Spell) && !HasAggro)
                return true;
            if (now < _nextSelfBladeturnTick || bot.IsOnStableMasterRoute ||
                CompanionFollowPolicy.WaitingForLeaderToStop(bot) ||
                !SelfBladeturnSafe(true, HasAggro, bot.IsAttacking,
                    bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true,
                    bot.IsStunned || bot.IsMezzed || bot.IsSilenced,
                    now - bot.LastAttackedByEnemyTick))
                return false;

            Spell shield = (bot.MiscSpells ?? []).Concat(bot.InstantMiscSpells ?? [])
                .Where(spell => IsSelfBladeturn(spell) && spell.Level <= bot.Level)
                .OrderByDescending(spell => spell.Level)
                .FirstOrDefault();
            if (shield == null)
            {
                _nextSelfBladeturnTick = now + 10_000;
                return false;
            }
            if (LivingHasEffect(bot, shield) ||
                shield.HasRecastDelay && bot.GetSkillDisabledDuration(shield) > 0 ||
                bot.Mana < bot.PowerCost(shield))
            {
                _nextSelfBladeturnTick = now + 1_000;
                return false;
            }

            if (bot.IsRecoveryResting)
                bot.WakeRecoveryRest();
            if (shield.CastTime > 0)
            {
                bot.StopMovingOnPath();
                bot.StopMoving();
            }
            GameObject previousTarget = bot.TargetObject;
            bot.TargetObject = bot;
            bool cast = CastCoordinatedBuff(shield, false);
            bot.TargetObject = previousTarget;
            _nextSelfBladeturnTick = now + (cast ? Math.Max(1_000, shield.CastTime + 500) : 5_000);
            return cast;
        }

        /// <summary>
        /// A group member (not a pet) within support reach that lacks this group or
        /// single-target buff and is beyond its range, closest first.
        /// </summary>
        private GameLiving FindOutOfRangeBuffTarget(Spell spell)
        {
            if (Body.Group == null || spell.IsPulsing ||
                spell.Target is not (eSpellTarget.REALM or eSpellTarget.GROUP))
                return null;
            int range = spell.Target == eSpellTarget.GROUP
                ? Math.Max(350, spell.Range)
                : spell.CalculateEffectiveRange(Body);
            return Body.Group.GetMembersInTheGroup()
                .Where(member => member != Body && member.IsAlive && InSupportReach(member) &&
                    !Body.IsWithinRadius(member, range) && !LivingHasEffect(member, spell))
                .OrderBy(member => Body.GetDistanceTo(member))
                .FirstOrDefault();
        }

        private bool IsStationaryTemporaryCompanionPerformer() =>
            IsTemporaryCompanionPerformer(BotBody) &&
            AssistedPlayer is { IsAlive: true, IsMoving: false } &&
            !BotBody.InCombat && !HasAggro && !BotBody.IsAttacking &&
            !CompanionFollowPolicy.WaitingForLeaderToStop(BotBody);

        private static bool IsTemporaryCompanionPerformer(GameBot bot) =>
            bot is { IsTemporaryGroupHelper: true, IsAutonomousWorldBot: false, IsPlayerLedGroup: true } &&
            IsClassicSongClass(bot);

        private void CompleteCompanionPerformerBuffBatch(GameBot bot)
        {
            if (!_companionPerformerBuffBatch || !IsTemporaryCompanionPerformer(bot))
                return;
            _companionPerformerBuffBatch = false;
            _nextSongTwistTick = 0;
        }

        private bool TryMaintainClassicSongTwist()
        {
            GameBot bot = BotBody;
            if (!IsClassicSongClass(bot) || !bot.IsAlive || HasResurrectionDuty())
                return false;

            bool temporaryCompanion = IsTemporaryCompanionPerformer(bot);
            bool ownerMoving = temporaryCompanion && AssistedPlayer?.IsMoving == true;
            if (_companionPerformerBuffBatch && temporaryCompanion)
            {
                if (ownerMoving || bot.InCombat || HasAggro || bot.IsAttacking)
                    CompleteCompanionPerformerBuffBatch(bot);
                else
                    return false;
            }

            if (bot.IsOnStableMasterRoute || bot.IsCasting ||
                bot.castingComponent.HasPendingSkillRequests ||
                bot.IsStunned || bot.IsMezzed || bot.IsSilenced ||
                GameLoop.GameLoopTime < _nextSongTwistTick)
                return false;

            bool groupedSupport = bot.Group?.MemberCount > 1;
            if (GameLoop.GameLoopTime >= _nextInstrumentKitCheck)
            {
                _nextInstrumentKitCheck = GameLoop.GameLoopTime + 60_000;
                if (BotStarterInstruments.Ensure(bot) && bot.IsAutonomousWorldBot)
                    AutonomousBotStatusPersistence.Queue(bot, true);
            }
            // Enhanced rest already verifies actual attacks, aggro, harmful
            // casts and pet/group combat. A lingering combat flag after the
            // fight must not stop the resting performer's harmless songs.
            bool immediateCombat = !bot.IsEnhancedResting && (bot.InCombat || HasAggro || bot.IsAttacking);

            // Bard and Skald preserve the last song's child buff, swap back to
            // their legal melee weapon, and fight in both solo and group play.
            // Autonomous Minstrels and grouped companions swap to melee;
            // only the legacy ungrouped companion keeps its ranged posture.
            // None of the three becomes a passive
            // support-only actor merely because a group exists.
            bool minstrelAtRange = !groupedSupport && !UsesMinstrelHybridCombat && (eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Minstrel &&
                                    !IsUnderImmediateMeleePressure();
            if (immediateCombat && !minstrelAtRange)
            {
                StopTwistedSong();
                SwitchToUsableMeleeWeapon();
                _nextSongTwistTick = GameLoop.GameLoopTime + 1_000;
                return false;
            }

            bool traveling = bot.IsMoving || bot.IsReturningAfterRelease ||
                             bot.Group?.LivingLeader?.IsMoving == true ||
                             bot.PersistentRecord?.Activity?.Contains("travel", StringComparison.OrdinalIgnoreCase) == true ||
                             bot.PersistentRecord?.Activity?.Contains("walking", StringComparison.OrdinalIgnoreCase) == true;

            IEnumerable<Spell> songCandidates = (bot.MiscSpells ?? [])
                .Concat(bot.InstantMiscSpells ?? [])
                .Where(spell => spell != null && spell.IsPulsing && !spell.IsHarmful &&
                                spell.Level <= bot.Level && IsMaintainableClassBuff(spell) &&
                                bot.Mana >= bot.PowerCost(spell) &&
                                (!ownerMoving || BotSongTwistPolicy.IsCompanionTravelSong(
                                    (eCharacterClass)bot.CharacterClass.ID, spell.SpellType)))
                .DistinctBy(spell => spell.ID)
                .Where(spell => spell.SpellType != eSpellType.SpeedEnhancement || !immediateCombat && (traveling || groupedSupport));
            if (ownerMoving)
                songCandidates = songCandidates.GroupBy(spell => spell.SpellType)
                    .Select(group => group.OrderByDescending(spell => spell.Level)
                        .ThenByDescending(spell => spell.Value).First());
            List<Spell> songs = songCandidates
                .OrderByDescending(spell => spell.SpellType == eSpellType.SpeedEnhancement && !immediateCombat)
                .ThenByDescending(spell => groupedSupport && spell.Target is eSpellTarget.GROUP or eSpellTarget.REALM)
                .ThenByDescending(spell => spell.Value)
                .ThenByDescending(spell => spell.Level)
                .ToList();

            if (songs.Count == 0)
            {
                // No currently affordable replacement is not a reason to cancel
                // a still-useful native pulse (nor bypass its own power checks).
                _nextSongTwistTick = GameLoop.GameLoopTime + 4_000;
                return false;
            }

            // The real pulse may have ended from interruption or lack of power.
            // Do not let a stale tracked ID permanently suppress its restart.
            ECSPulseEffect activePulse = Body.effectListComponent.GetPulseEffects()
                .FirstOrDefault(effect => !effect.IsEnding && !effect.IsEnded &&
                    effect.SpellHandler?.Spell is Spell activeSpell && activeSpell.IsPulsing &&
                    !activeSpell.IsHarmful && IsMaintainableClassBuff(activeSpell));
            _activeTwistedSongId = activePulse?.SpellHandler.Spell.ID ?? 0;
            int chosenId = BotSongTwistPolicy.Choose(songs[0].ID, _activeTwistedSongId,
                songs.Select(candidate => new BotSongTwistPolicy.Song(candidate.ID, candidate.CastTime,
                    bot.GetSkillDisabledDuration(candidate), SongRemainingMilliseconds(candidate))).ToArray());
            Spell song = songs.FirstOrDefault(candidate => candidate.ID == chosenId);

            // Preserve the anchor. Only leave after its reuse clears and a real
            // child pulse has enough life for the secondary and return casts.
            if (song == null)
            {
                _nextSongTwistTick = GameLoop.GameLoopTime + 750;
                return false;
            }

            if (song.NeedInstrument && !TryEquipRealInstrument(bot, song.InstrumentRequirement))
            {
                _nextSongTwistTick = GameLoop.GameLoopTime + 5_000;
                return false;
            }

            // Switching away from an instrument ends that song's source, but
            // not its lingering child buffs. Otherwise let the native effect
            // list replace the old source only when the new song actually takes;
            // an asynchronously rejected request must not silence the anchor.
            if (activePulse?.SpellHandler.Spell is Spell previousSong && previousSong.NeedInstrument &&
                bot.ActiveWeapon?.DPS_AF != previousSong.InstrumentRequirement)
                activePulse.End();

            GameObject previousTarget = bot.TargetObject;
            GameLiving target = FindMissingMaintenanceTarget(song) ?? bot;
            bot.TargetObject = target;
            bool cast = bot.CastSpell(song, m_mobSpellLine, false);
            bot.TargetObject = previousTarget;
            if (cast)
            {
                _activeTwistedSongId = song.ID;
            }

            // Actual casts, child lifetimes and skill reuse own the timing, not
            // a fixed two-second toggle. No additional timer/service is created.
            _nextSongTwistTick = GameLoop.GameLoopTime + (cast ? 250 : 1_000);
            // Queuing a legal mobile song must not consume the movement turn.
            // Cast selectors still see the pending/active request and cannot
            // enqueue another spell; follow/route decisions may run alongside it.
            return cast && !immediateCombat && !BotSongTwistPolicy.IsMobileSong(bot, song);
        }

        private long SongRemainingMilliseconds(Spell song)
        {
            eEffect type = EffectHelper.GetEffectFromSpell(song);
            IEnumerable<GameLiving> members = song.Target is eSpellTarget.GROUP or eSpellTarget.REALM
                ? Body.Group?.GetMembersInTheGroup() ?? [Body] : [Body];
            return members.Where(member => member.IsAlive && Body.IsWithinRadius(member, Math.Max(350, song.Range)))
                .Select(member => member.effectListComponent.GetSpellEffects(type)
                    .Where(effect => (effect.IsActive || effect.IsStarting) && !effect.IsEnding &&
                        effect.SpellHandler?.Spell.SpellType == song.SpellType)
                    .Select(effect => effect.Duration == 0 ? long.MaxValue : Math.Max(0, effect.ExpireTick - GameLoop.GameLoopTime))
                    .DefaultIfEmpty(0).Max())
                .DefaultIfEmpty(0).Min();
        }

        private void TryMaintainTankChant()
        {
            GameBot bot = BotBody;
            if (bot?.CharacterClass == null ||
                (eCharacterClass)bot.CharacterClass.ID is not (eCharacterClass.Paladin or eCharacterClass.Warden) ||
                !bot.IsAlive || bot.IsOnStableMasterRoute || bot.IsCasting || HasResurrectionDuty() ||
                bot.castingComponent.HasPendingSkillRequests || bot.IsCrowdControlled || bot.IsSilenced ||
                GameLoop.GameLoopTime < _nextSongTwistTick)
                return;

            _nextSongTwistTick = GameLoop.GameLoopTime + 750;
            List<Spell> chants = (bot.MiscSpells ?? []).Concat(bot.InstantMiscSpells ?? [])
                .Where(spell => spell?.Level <= bot.Level && BotSongTwistPolicy.IsManagedSong(bot, spell))
                .GroupBy(spell => spell.SpellType)
                .Select(group => group.OrderByDescending(spell => spell.Level).First()).ToList();
            if (chants.Count == 0) return;
            Spell active = bot.effectListComponent.GetPulseEffects()
                .FirstOrDefault(effect => !effect.IsEnding && !effect.IsEnded &&
                    BotSongTwistPolicy.IsManagedSong(bot, effect.SpellHandler?.Spell))?.SpellHandler.Spell;
            Spell chosen;
            if ((eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Warden)
            {
                bool combat = bot.InCombat || HasAggro || bot.IsAttacking ||
                    bot.Group?.GetMembersInTheGroup().Any(member => member.IsAlive &&
                        member.CurrentRegionID == bot.CurrentRegionID &&
                        bot.IsWithinRadius(member, GROUP_DEFENSE_ASSIST_RADIUS) && member.InCombat) == true;
                bool traveling = bot.IsMoving || bot.IsReturningAfterRelease || bot.Group?.LivingLeader?.IsMoving == true;
                eSpellType anchor = BotSongTwistPolicy.WardenAnchor(traveling, combat,
                    bot.Group?.MemberCount > 1, chants.Any(spell => spell.SpellType == eSpellType.Bladeturn));
                // A faster speed in the group (a performer's song) replaces the
                // Warden's travel chant; it keeps bladeturn or damage add instead.
                Spell speedChant = chants.FirstOrDefault(spell => spell.SpellType == eSpellType.SpeedEnhancement);
                if (anchor == eSpellType.SpeedEnhancement && speedChant != null && GroupmateHasStrongerSpeed(speedChant))
                {
                    chants.Remove(speedChant);
                    anchor = eSpellType.Bladeturn;
                }
                chosen = chants.FirstOrDefault(spell => spell.SpellType == anchor) ??
                    chants.FirstOrDefault(spell => spell.SpellType == eSpellType.Bladeturn) ??
                    chants.FirstOrDefault(spell => spell.SpellType == eSpellType.DamageAdd);
                // No PBT round trips: its native recast can exceed child lifetime.
                // Wait for the selected anchor; do not spam/toggle the active one.
            }
            else
            {
                bool injured = bot.HealthPercent < 95 || bot.Group?.GetMembersInTheGroup().Any(member =>
                    member.IsAlive && member.HealthPercent < 95 && member.CurrentRegionID == bot.CurrentRegionID &&
                    bot.IsWithinRadius(member, GROUP_DEFENSE_ASSIST_RADIUS)) == true;
                bool combatHealing = injured && (bot.InCombat || HasAggro || bot.IsAttacking);
                // A solo Paladin holds one chant instead of cycling three: each recast briefly
                // retargets the bot to itself mid-fight, and the held chant was endurance or
                // armor, not damage (solo Paladins killed at half the Armsman rate, Oct 3-5).
                bool fighting = bot.InCombat || HasAggro || bot.IsAttacking;
                if ((bot.Group?.MemberCount ?? 1) <= 1)
                {
                    eSpellType anchor = BotSongTwistPolicy.SoloPaladinAnchor(fighting, bot.HealthPercent);
                    chosen = chants.FirstOrDefault(spell => spell.SpellType == anchor) ??
                        chants.FirstOrDefault(spell => spell.SpellType == eSpellType.DamageAdd) ??
                        chants.FirstOrDefault(spell => spell.SpellType != eSpellType.CombatHeal);
                    if (chosen == null || chosen.ID == active?.ID || bot.GetSkillDisabledDuration(chosen) > 0 ||
                        bot.Mana < bot.PowerCost(chosen)) return;
                    GameObject soloTarget = bot.TargetObject;
                    try { bot.TargetObject = bot; bot.CastSpell(chosen, m_mobSpellLine, false); }
                    finally { bot.TargetObject = soloTarget; }
                    return;
                }
                chants = chants.Where(spell => spell.SpellType != eSpellType.CombatHeal || combatHealing)
                    .OrderByDescending(spell => spell.SpellType == eSpellType.EnduranceRegenBuff)
                    .ThenByDescending(spell => spell.SpellType == eSpellType.SpecArmorFactorBuff)
                    .ThenByDescending(spell => spell.SpellType == eSpellType.DamageAdd).ToList();
                if (chants.Count == 0) return;
                int id = BotSongTwistPolicy.Choose(chants[0].ID, active?.ID ?? 0,
                    chants.Select(spell => new BotSongTwistPolicy.Song(spell.ID, spell.CastTime,
                        bot.Mana >= bot.PowerCost(spell) ? bot.GetSkillDisabledDuration(spell) : int.MaxValue,
                        SongRemainingMilliseconds(spell))).ToArray());
                chosen = chants.FirstOrDefault(spell => spell.ID == id);
            }

            if (chosen == null || chosen.ID == active?.ID || bot.GetSkillDisabledDuration(chosen) > 0 ||
                bot.Mana < bot.PowerCost(chosen)) return;
            GameObject oldTarget = bot.TargetObject;
            try
            {
                bot.TargetObject = bot;
                bot.CastSpell(chosen, m_mobSpellLine, false);
            }
            finally { bot.TargetObject = oldTarget; }
        }

        private bool IsUnderImmediateMeleePressure() =>
            AggroList.Keys.Any(attacker => attacker?.IsAlive == true &&
                                           Body.IsWithinRadius(attacker, Body.MeleeAttackRange + 35));

        private void StopTwistedSong()
        {
            if (Body == null)
                return;

            // End the maintained pulse, not the short-duration child buffs it
            // already applied. Scanning also adopts/corrects a song that was
            // started before this brain began tracking its rotation.
            foreach (ECSPulseEffect active in Body.effectListComponent.GetPulseEffects()
                         .Where(effect => effect?.SpellHandler?.Spell is Spell spell &&
                                          spell.IsPulsing && !spell.IsHarmful &&
                                          IsMaintainableClassBuff(spell))
                         .ToList())
            {
                active.End();
            }
            _activeTwistedSongId = 0;
        }

        private static bool IsClassicSongClass(GameBot bot) => bot?.CharacterClass != null &&
            IsClassicSongClass((eCharacterClass)bot.CharacterClass.ID);

        public static bool IsClassicSongClass(eCharacterClass characterClass) => characterClass is
            eCharacterClass.Bard or eCharacterClass.Minstrel or eCharacterClass.Skald;

        private GameLiving FindMissingMaintenanceTarget(Spell spell)
        {
            if (spell.Target is eSpellTarget.PET or eSpellTarget.CONTROLLED)
            {
                GameLiving pet = Body.ControlledBrain?.Body;
                return pet?.IsAlive == true && Body.IsWithinRadius(pet, Math.Max(350, spell.Range)) && !LivingHasEffect(pet, spell)
                    ? pet
                    : null;
            }

            if (spell.Target == eSpellTarget.SELF)
                return LivingHasEffect(Body, spell) ? null : Body;

            if (spell.Target == eSpellTarget.GROUP)
            {
                // A moving native-speed companion only needs to start its own
                // pulse. A group member briefly missing the child effect is
                // not grounds to stop again and restart the three-second cast.
                if (CompanionFollowPolicy.IsNativeCompanionTravelSpeed(BotBody, spell))
                    return LivingHasEffect(Body, spell) ? null : Body;
                IEnumerable<GameLiving> members = Body.Group?.GetMembersInTheGroup() ?? [Body];
                return members.Any(member => member.IsAlive && Body.IsWithinRadius(member, Math.Max(350, spell.Range)) && !LivingHasEffect(member, spell)) ||
                       FindMissingPartyPetBuffTarget(spell) != null
                    ? Body
                    : null;
            }

            if (spell.Target == eSpellTarget.REALM)
            {
                if (!spell.IsPulsing)
                    return FindRandomMissingRealmBuffTarget(spell, 350);
                if (!LivingHasEffect(Body, spell))
                    return Body;
                return Body.Group?.GetMembersInTheGroup()
                    .FirstOrDefault(member => member != Body && member.IsAlive &&
                                              Body.IsWithinRadius(member, Math.Max(350, spell.Range)) &&
                                              !LivingHasEffect(member, spell)) ?? FindMissingPartyPetBuffTarget(spell);
            }

            return null;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, BotBuffReservations<GameLiving>> BuffClaims = new();

        private GameLiving FindRandomMissingRealmBuffTarget(Spell spell, int minimumRange = 0)
        {
            Group group = Body.Group;
            var claims = group == null ? null : BuffClaims.GetValue(AutonomousRealmRaid.SupportScope(BotBody), _ => new());
            int family = (int)EffectHelper.GetEffectFromSpell(spell);
            long now = GameLoop.GameLoopTime;
            int range = Math.Max(minimumRange, spell.CalculateEffectiveRange(Body));
            IEnumerable<GameLiving> members = spell.Target == eSpellTarget.REALM ? AutonomousRealmRaid.SupportMembers(BotBody) : group?.GetMembersInTheGroup() ?? [Body];
            // One pool: players, companions, world bots and valid attached pets.
            // Do not leave pets until every humanoid has been processed first.
            return BotBuffReservations<GameLiving>.Choose(
                members.Concat(BotGroupPetBuffTargets.Enumerate(BotBody, spell)),
                target => target.IsAlive && Body.IsWithinRadius(target, range) &&
                    !LivingHasEffect(target, spell) && !(claims?.IsReserved(target, family, now) ?? false));
        }

        private bool CastCoordinatedBuff(Spell spell, bool? queued = null)
        {
            Group group = Body.Group;
            GameLiving target = Body.TargetObject as GameLiving;
            bool shared = group != null && target != null && spell.Target == eSpellTarget.REALM &&
                !spell.IsPulsing && IsMaintainableClassBuff(spell);
            BotBuffReservations<GameLiving> claims = shared ? BuffClaims.GetValue(AutonomousRealmRaid.SupportScope(BotBody), _ => new()) : null;
            int family = shared ? (int)EffectHelper.GetEffectFromSpell(spell) : 0;
            if (shared && (LivingHasEffect(target, spell) ||
                !claims.TryReserve(Body, target, family, GameLoop.GameLoopTime, spell.CastTime)))
                return false;
            bool cast = false;
            try
            {
                cast = queued.HasValue ? Body.CastSpell(spell, m_mobSpellLine, queued.Value) : Body.CastSpell(spell, m_mobSpellLine);
                return cast;
            }
            finally
            {
                if (shared && !cast) claims.Release(Body, target, family);
            }
        }

        private GameLiving FindMissingPartyPetBuffTarget(Spell spell)
        {
            GameNPC pet = BotGroupPetBuffTargets.Enumerate(BotBody, spell)
                .FirstOrDefault(candidate => !LivingHasEffect(candidate, spell));
            // Native group spells fan out from the caster; realm buffs target one pet.
            return pet == null ? null : spell.Target == eSpellTarget.GROUP ? Body : pet;
        }

        internal static bool TryEquipRealInstrument(GameLiving bot, int requiredInstrument)
        {
            bool CorrectInstrument(DbInventoryItem item) =>
                item?.Object_Type == (int)eObjectType.Instrument &&
                (requiredInstrument <= 0 || item.DPS_AF == requiredInstrument);

            if (CorrectInstrument(bot.ActiveWeapon))
                return true;

            DbInventoryItem distance = bot.Inventory?.GetItem(eInventorySlot.DistanceWeapon);
            if (CorrectInstrument(distance))
            {
                bot.SwitchWeapon(eActiveWeaponSlot.Distance);
                return true;
            }

            DbInventoryItem twoHand = bot.Inventory?.GetItem(eInventorySlot.TwoHandWeapon);
            if (CorrectInstrument(twoHand))
            {
                bot.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                return true;
            }

            DbInventoryItem packed = bot.Inventory?.AllItems.FirstOrDefault(item =>
                item != null && item.SlotPosition >= (int)eInventorySlot.FirstBackpack &&
                item.SlotPosition <= (int)eInventorySlot.LastBackpack && CorrectInstrument(item));
            if (packed != null && bot.Inventory.MoveItem(
                    (eInventorySlot)packed.SlotPosition,
                    eInventorySlot.TwoHandWeapon,
                    packed.Count))
            {
                bot.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                return CorrectInstrument(bot.ActiveWeapon);
            }

            return false;
        }

        /// <summary>
        /// The Spiritmaster's one-minute damage shields (Boon of the Fallen and
        /// its ranks). Cast as routine upkeep they ran out before the next fight
        /// and cost the bot a 4-second cast each time, so they are cast only in
        /// combat. Pet focus shields (pulsing) and longer shields are unchanged.
        /// </summary>
        public static bool IsShortCombatShield(Spell spell) =>
            spell?.SpellType == eSpellType.DamageShield && !spell.IsConcentration && !spell.IsPulsing &&
            spell.Target != eSpellTarget.PET && spell.Duration > 0 && spell.Duration <= 60_000;

        /// <summary>
        /// In a fight: shield the pet that is fighting, otherwise the bot itself
        /// when the enemy is attacking it. One cast per expiry, never out of combat.
        /// </summary>
        private bool TryShortCombatShield()
        {
            if (Body.MiscSpells == null || Body.IsCasting || !Body.CanCastMiscSpells || Body.TargetObject is not GameLiving enemy)
                return false;

            Spell shield = Body.MiscSpells
                .Where(spell => IsShortCombatShield(spell) && !AutonomousPetSupport.IsDisabledBotDamageShield(BotBody, spell) &&
                                CanCastDefensiveSpell(spell))
                .OrderByDescending(spell => spell.Level)
                .FirstOrDefault();
            if (shield == null)
                return false;

            GameLiving pet = Body.ControlledBrain?.Body;
            GameLiving target =
                pet != null && pet.IsAlive && pet.InCombat && !LivingHasEffect(pet, shield) &&
                Body.IsWithinRadius(pet, shield.CalculateEffectiveRange(Body)) ? pet :
                Body.InCombat && enemy.TargetObject == Body && !LivingHasEffect(Body, shield) ? Body : null;
            if (target == null)
                return false;

            GameObject oldTarget = Body.TargetObject;
            Body.TargetObject = target;
            bool cast = Body.CastSpell(shield, m_mobSpellLine);
            Body.TargetObject = oldTarget;
            return cast;
        }

        internal static bool IsMaintainableClassBuff(Spell spell) => spell?.SpellType switch
        {
            eSpellType.SpeedEnhancement or
            eSpellType.BodySpiritEnergyBuff or eSpellType.HeatColdMatterBuff or
            eSpellType.SpiritResistBuff or eSpellType.EnergyResistBuff or eSpellType.HeatResistBuff or
            eSpellType.ColdResistBuff or eSpellType.BodyResistBuff or eSpellType.MatterResistBuff or
            eSpellType.AllMagicResistBuff or eSpellType.EnduranceRegenBuff or eSpellType.PowerRegenBuff or
            eSpellType.AblativeArmor or eSpellType.AcuityBuff or eSpellType.AFHitsBuff or
            eSpellType.ArmorAbsorptionBuff or eSpellType.BaseArmorFactorBuff or eSpellType.SpecArmorFactorBuff or
            eSpellType.PaladinArmorFactorBuff or eSpellType.Buff or eSpellType.CelerityBuff or
            eSpellType.ConstitutionBuff or eSpellType.CourageBuff or eSpellType.CrushSlashTrustBuff or
            eSpellType.DexterityBuff or eSpellType.DexterityQuicknessBuff or eSpellType.EffectivenessBuff or
            eSpellType.FatigueConsumptionBuff or eSpellType.FlexibleSkillBuff or eSpellType.HasteBuff or
            eSpellType.HealthRegenBuff or eSpellType.HeroismBuff or eSpellType.MagicResistBuff or
            eSpellType.MeleeDamageBuff or eSpellType.MLABSBuff or eSpellType.ParryBuff or
            eSpellType.PowerHealthEnduranceRegenBuff or eSpellType.StrengthBuff or
            eSpellType.StrengthConstitutionBuff or eSpellType.SuperiorCourageBuff or
            eSpellType.ToHitBuff or eSpellType.WeaponSkillBuff or eSpellType.DamageAdd or
            eSpellType.OffensiveProc or eSpellType.DefensiveProc or eSpellType.DamageShield or
            eSpellType.Bladeturn => true,
            _ => false
        };

        /// <summary>
        /// Another living groupmate provides a faster speed: a bot that knows one
        /// (it will run it), or a real player who is running one right now.
        /// From the stefanrows/OfflineDAoC fork (0.200.0).
        /// </summary>
        private bool GroupmateHasStrongerSpeed(Spell speed)
        {
            if (Body?.Group == null || speed == null)
                return false;
            foreach (GameLiving member in Body.Group.GetMembersInTheGroup())
            {
                if (member == Body || !member.IsAlive)
                    continue;
                if (member is GamePlayer player)
                {
                    if (player.effectListComponent.GetPulseEffects().Any(effect =>
                            !effect.IsEnding && !effect.IsEnded &&
                            effect.SpellHandler?.Spell is Spell running &&
                            running.SpellType == eSpellType.SpeedEnhancement && running.Value > speed.Value))
                        return true;
                    continue;
                }
                if (member is not GameBot mate)
                    continue;
                if ((mate.MiscSpells ?? []).Concat(mate.InstantMiscSpells ?? [])
                    .Any(spell => spell != null && spell.SpellType == eSpellType.SpeedEnhancement &&
                                  spell.Level <= mate.Level && spell.Value > speed.Value))
                    return true;
            }
            return false;
        }

        private eAutonomousThinkMode CurrentThinkMode() => HasAggro || Body?.InCombat == true
                ? eAutonomousThinkMode.Combat
                : BotBody?.IsPlayerLedGroup == true
                    ? eAutonomousThinkMode.PlayerLed
                    : BotBody?.IsRecoveryResting == true
                        ? eAutonomousThinkMode.Resting
                        : Body?.IsMoving == true || AutonomousGroupMotion.GroupTraveling(BotBody)
                            ? eAutonomousThinkMode.Travel
                            : eAutonomousThinkMode.Planning;

        private eAutonomousFidelity CurrentFidelity()
        {
            if (BotBody?.IsPlayerLedGroup == true)
                return eAutonomousFidelity.NearbyHuman;

            long now = GameLoop.GameLoopTime;
            if (now < _nextFidelityCheckTick)
                return _cachedFidelity;
            _nextFidelityCheckTick = now + 5_000 + (Body?.ObjectID ?? 0) % 1_500;
            if (Body?.IsVisibleToPlayers != true)
                return _cachedFidelity = eAutonomousFidelity.Efficient;
            return _cachedFidelity = Body.GetPlayersInRadius(6000).Count > 0
                ? eAutonomousFidelity.NearbyHuman
                : eAutonomousFidelity.Standard;
        }

        public override void KillFSM()
        {
            FSM.KillFSM();
        }

        public override void Notify(DOLEvent e, object sender, EventArgs args)
        {
            if (e == GameLivingEvent.AttackedByEnemy && args is AttackedByEnemyEventArgs attackArgs)
            {
                OnAttackedByEnemy(attackArgs.AttackData);
            }
        }

        #endregion

        #region FSM States

        private class BotState : FSMState
        {
            protected BotBrain _brain;

            public BotState(BotBrain brain) : base()
            {
                _brain = brain;
            }

            public override void Enter() { }
            public override void Exit() { }
            public override void Think() { }
        }

        private class BotState_Idle : BotState
        {
            public BotState_Idle(BotBrain brain) : base(brain)
            {
                StateType = eFSMStateType.IDLE;
            }

            public override void Enter()
            {
                if (_brain.Body != null)
                {
                    _brain.Body.StopFollowing();
                    _brain.Body.StopAttack();
                }
            }

            public override void Think()
            {
                _brain.AlreadyCheckedHeals = false;

                if (_brain.HasAggro)
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }

                GamePlayer leader = _brain.AssistedPlayer;
                if (leader != null && leader.IsAlive)
                {
                    // Check if owner is attacking something
                    if (leader.TargetObject is GameLiving ownerTarget
                        && !BotPartyRoles.IsSupport(_brain.BotBody)
                        && PlayerLedPullCoordinator.Available(_brain.BotBody, leader)
                        && _brain.Body.IsWithinRadius(ownerTarget, GROUP_DEFENSE_ASSIST_RADIUS)
                        && (leader.IsAttacking || (leader.IsCasting && leader.castingComponent?.SpellHandler?.Spell?.IsHarmful == true))
                        && _brain.CanAggroTarget(ownerTarget))
                    {
                        _brain.AddToAggroList(ownerTarget, 1);
                        _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                        return;
                    }

                    if (!_brain.Body.IsWithinRadius(leader, 650))
                    {
                        _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);
                        return;
                    }
                }

                _brain.CheckSpells(eCheckSpellType.Defensive);
            }
        }

        private class BotState_Follow : BotState
        {
            public BotState_Follow(BotBrain brain) : base(brain)
            {
                StateType = eFSMStateType.FOLLOW;
            }

            public override void Enter()
            {
                _brain.FollowFormation();
            }

            public override void Think()
            {
                _brain.AlreadyCheckedHeals = false;

                if (_brain.HasAggro)
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                    return;
                }

                // Persistent world bots have no human leader. Their release walk
                // must run before the ordinary owner/leader guard or they would
                // enter IDLE immediately and remain stranded at the bind point.
                if (_brain.BotBody.ContinueReturnJourney())
                    return;

                GamePlayer leader = _brain.AssistedPlayer;
                if (leader == null || !leader.IsAlive)
                {
                    _brain.FSM.SetCurrentState(eFSMStateType.IDLE);
                    return;
                }

                // Check if owner is in combat or attacking something
                if (leader.IsAttacking || (leader.IsCasting && leader.castingComponent?.SpellHandler?.Spell?.IsHarmful == true))
                {
                    // Try to get owner's target
                    if (leader.TargetObject is GameLiving ownerTarget
                        && !BotPartyRoles.IsSupport(_brain.BotBody)
                        && PlayerLedPullCoordinator.Available(_brain.BotBody, leader)
                        && _brain.Body.IsWithinRadius(ownerTarget, GROUP_DEFENSE_ASSIST_RADIUS)
                        && ownerTarget.IsAlive
                        && _brain.CanAggroTarget(ownerTarget))
                    {
                        _brain.AddToAggroList(ownerTarget, 1);
                        _brain.FSM.SetCurrentState(eFSMStateType.AGGRO);
                        return;
                    }

                    // If owner is in combat but we don't have a valid target, check recent attackers
                }

                // No combat — heal/buff
                if (_brain.CheckHeals())
                {
                    _brain.FollowDuringMobileSong();
                    return;
                }

                // A native caster-speed pulse is not a mobile performer song.
                // Keep the temporary companion planted for the real cast; the
                // next FOLLOW pulse resumes formation as soon as it finishes.
                if (CompanionFollowPolicy.NativeTravelSpeedCastInProgress(_brain.BotBody))
                    return;

                // Temporary companions already passed through the strict rest
                // preflight before songs, buffs, pets, roleplay, and this FSM.
                // Keep the autonomous threshold policy byte-for-byte scoped to
                // non-temporary playerbots until companion behavior is proven.
                if (!_brain.BotBody.IsTemporaryGroupHelper)
                {
                    bool rest = AutonomousRestPolicy.ShouldRest(
                        leader.CurrentRegionID == _brain.Body.CurrentRegionID &&
                            _brain.Body.IsWithinRadius(leader, 350),
                        AutonomousRestPolicy.MovementBlocksRest(
                            _brain.Body.IsMoving,
                            leader.IsMoving,
                            _brain._ambientWanderMovement),
                        BotRestRecovery.BlocksRest(_brain.BotBody),
                        _brain.BotBody.IsRecoveryResting,
                        _brain.Body.HealthPercent,
                        _brain.Body.ManaPercent,
                        _brain.Body.EndurancePercent,
                        _brain.Body.MaxMana > 0);
                    if (rest)
                        _brain._ambientWanderMovement = false;
                    if (rest)
                        _brain.BotBody.BeginRecoveryRest();
                    else
                        _brain.BotBody.WakeRecoveryRest();
                    if (rest)
                        return;
                }

                // Ambient movement is deliberately the lowest priority. Healing,
                // pet maintenance, resting and defensive abilities all finish first.
                if (!_brain.Body.InCombat && _brain.CheckSpells(eCheckSpellType.Defensive))
                {
                    _brain.FollowDuringMobileSong();
                    return;
                }

                // Idle bots stand in formation; there is no idle wandering (it walked
                // at 40% speed). Idle chat around a resting leader is kept.
                _brain.FollowFormation();
                if (_brain.CanAmbientWander && !_brain.Body.IsCasting)
                    _brain.MaybeIdleRoleplay();
            }

            public override void Exit()
            {
                _brain.Body.StopFollowing();
            }
        }

        private class BotState_Aggro : BotState
        {
            private const int LEAVE_WHEN_OUT_OF_COMBAT_FOR = 6000;
            private long _aggroEndTime;

            public BotState_Aggro(BotBrain brain) : base(brain)
            {
                StateType = eFSMStateType.AGGRO;
            }

            public override void Enter()
            {
                _brain._ambientWanderMovement = false;
                // The first-contact window starts at the state's first think, not when it was set:
                // a pull or raid call ordered between thinks (travel/planning intervals reach ~10 s)
                // otherwise expired before the bot ever acted on it (run 9/10 pull loops, raid
                // target calls that never landed on Summoner Cunovinda).
                _aggroEndTime = long.MaxValue;
            }

            public override void Exit()
            {
                _brain.Body.StopAttack();
                _brain.Body.TargetObject = null;
                _brain.ClearAggroList();
            }

            public override void Think()
            {
                _brain.AlreadyCheckedHeals = false;
                if (_aggroEndTime == long.MaxValue)
                    _aggroEndTime = GameLoop.GameLoopTime + LEAVE_WHEN_OUT_OF_COMBAT_FOR;

                if (_brain._returnToFormationAfterPull && _brain.ActiveOrderedPullTarget == null &&
                    _brain.CalculateNextAttackTarget() == null)
                {
                    _brain._returnToFormationAfterPull = false;
                    if (_brain.Body.IsCasting)
                        _brain.Body.StopCurrentSpellcast();
                    _brain.Body.StopAttack();
                    _brain.Body.TargetObject = null;
                    _brain.ClearAggroList();
                    _brain.FSM.SetCurrentState(_brain.AssistedPlayer?.IsAlive == true
                        ? eFSMStateType.FOLLOW
                        : eFSMStateType.IDLE);
                    return;
                }

                if (_brain.ActiveOrderedPullTarget == null &&
                    (!_brain.HasAggro || (!_brain.HasCommittedDungeonPull &&
                        !_brain.Body.InCombatInLast(LEAVE_WHEN_OUT_OF_COMBAT_FOR) && GameServiceUtils.ShouldTick(_aggroEndTime))))
                {
                    if (!_brain.Body.IsMezzed && !_brain.Body.IsStunned)
                    {
                        if (_brain.AssistedPlayer != null && _brain.AssistedPlayer.IsAlive)
                            _brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);
                        else
                            _brain.FSM.SetCurrentState(eFSMStateType.IDLE);

                        return;
                    }
                }

                // Passive stance: heals only, no attacking
                if (_brain.BotBody.Stance == eBotStance.Passive)
                {
                    _brain.CheckHeals();
                    return;
                }

                if (_brain.HoldsExclusiveSupportRole())
                {
                    _brain.PerformGroupSupport();
                }
                else if (_brain.IsHealer)
                {
                    if (!_brain.CheckHeals())
                        _brain.AttackMostWanted();
                }
                else
                    _brain.AttackMostWanted();
            }
        }

        private class BotState_Passive : BotState
        {
            public BotState_Passive(BotBrain brain) : base(brain)
            {
                StateType = eFSMStateType.PASSIVE;
            }

            public override void Enter()
            {
                _brain.Body.StopAttack();
                _brain.Body.StopFollowing();
                _brain.ClearAggroList();
            }

            public override void Think()
            {
                _brain.AlreadyCheckedHeals = false;

                if (_brain.IsHealer)
                    _brain.CheckHeals();
            }
        }

        #endregion

        #region Combat

        public virtual void AttackMostWanted()
        {
            if (!IsActive)
                return;

            if (AutonomousDefensivePull.Hold(BotBody))
            {
                if (!Body.IsCasting) CheckHeals();
                return;
            }

            EnforceCompanionEngagementRange();

            if (HoldsExclusiveSupportRole())
            {
                PerformGroupSupport();
                return;
            }

            // The native handler owns an accepted cast through completion (and
            // handles real hits, range/LOS loss and death). Do not replace it
            // with a melee/range/pull decision merely because an enemy is near.
            // This also preserves legal uninterruptible casts.
            if (Body.IsCasting &&
                !SavageBotCombatPolicy.MayAttackDuringActiveCast(
                    (eCharacterClass)BotBody.CharacterClass.ID,
                    Body.castingComponent?.SpellHandler?.Spell))
                return;

            GameLiving protectionTarget = FindProtectionTarget();
            if (protectionTarget != null)
                AddToAggroList(protectionTarget, 10000);

            GameLiving directAttacker = RecentDirectAttacker();

            // Bow release is driven by AttackAction, not by this brain pulse.
            // Re-entering the general target/movement decision while Aim is
            // pending is what produced the rapid draw animation with no arrow.
            // A close attacker or a group-protection emergency still breaks the
            // guard and follows the existing defensive/melee behavior.
            // A stalled close-range draw (see RequestArcherMeleeFallback) no
            // longer owns the turn, so the melee fallback below can engage.
            if (!ArcherMeleeFallbackActive && Body.TargetObject is GameLiving currentBowTarget &&
                BotRangedCombat.BowDrawOwnsDecision(
                    (eCharacterClass)BotBody.CharacterClass.ID,
                    Body.attackComponent?.AttackState == true,
                    Body.ActiveWeaponSlot,
                    currentBowTarget.IsAlive && currentBowTarget.ObjectState == GameObject.eObjectState.Active &&
                    (currentBowTarget is not DOL.GS.Keeps.GuardLord || Body.IsWithinRadius(currentBowTarget, 280)),
                    Body.attackComponent != null && Body.IsWithinRadius(currentBowTarget, Body.attackComponent.AttackRange),
                    directAttacker != null && Body.IsWithinRadius(directAttacker, Body.MeleeAttackRange + 200),
                    protectionTarget != null))
                return;

            GameObject previousAttackTarget = Body.TargetObject;
            GameLiving activePetTarget = BotBody?.IsAutonomousWorldBot == true && !BotBody.IsPlayerLedGroup
                ? AutonomousPetSupport.ActiveOwnedPetCombatTarget(BotBody)
                : null;
            if (activePetTarget != null)
                AddToAggroList(activePetTarget, Math.Max(1, activePetTarget.EffectiveLevel));
            // Once our own pet has made contact, keep the owner and pet on that
            // same live opponent unless the owner is personally attacked or is
            // protecting a group member. Generic aggro may still contain the
            // previous pull and used to make archers/casters repeatedly switch
            // away from the fight their pet had already started.
            Body.TargetObject = protectionTarget ?? directAttacker ?? activePetTarget ?? CalculateNextAttackTarget();
            // A tethered boss outside its tether is immune: stop hitting it and lead it home (AutonomousTetherReset).
            if (Body.TargetObject is GameNPC tetheredBoss && AutonomousTetherReset.TryHandle(BotBody, tetheredBoss))
                return;

            if (Body.TargetObject is GameLiving wallTarget && AutonomousRvrDefense.HoldWall(BotBody, wallTarget))
            {
                if (BotRangedCombat.IsDedicatedArcher((eCharacterClass)BotBody.CharacterClass.ID) &&
                    BotBody.Inventory.GetItem(eInventorySlot.DistanceWeapon) != null &&
                    Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance)
                    BotBody.SwitchWeapon(eActiveWeaponSlot.Distance);
                int defenseRange = PrefersCurrentSpellRange() ? OffensiveApproachRange(wallTarget) :
                    Body.attackComponent.AttackRange;
                if (!AutonomousRvrDefense.IsCommittedDefender(BotBody) && (!Body.IsWithinRadius(wallTarget, defenseRange) ||
                    !PathfindingProvider.Instance.HasLineOfSight(Body.CurrentZone, new(Body.X, Body.Y, Body.Z),
                        new(wallTarget.X, wallTarget.Y, wallTarget.Z), PathfindingProvider.Instance.DefaultFilters)))
                {
                    Body.StopAttack();
                    Body.StopFollowing();
                    Body.StopMovingOnPath();
                    Body.StopMoving();
                    CheckHeals();
                    return;
                }
            }

            if (Body.TargetObject != null)
            {
                if (BotBody.IsAutonomousWorldBot && Body.TargetObject is DOL.GS.Keeps.GuardLord lord &&
                    !Body.IsWithinRadius(lord, 280))
                {
                    Body.StopAttack();
                    Body.PathTo(new Point3D(lord.X, lord.Y, lord.Z), Body.MaxSpeed);
                    return;
                }
                if (Body.TargetObject is GameLiving ambushTarget && TryApproachAmbush(ambushTarget))
                    return;
                if (Body.IsStealthed)
                    Body.Stealth(false);

                if (Body.TargetObject is GameLiving pullTarget && pullTarget == ActiveOrderedPullTarget &&
                    ApproachPlayerLedPullTarget(pullTarget))
                    return;

                bool rangedCaster = PrefersCurrentSpellRange();

                // A melee bot already chasing its own target at stick range keeps
                // that order. Clearing it every think made NpcStartAttack stop the
                // running bot and re-follow, which clients saw as rubberbanding.
                // A caster meleeing because it is out of power chases like a melee bot.
                bool outOfPowerMelee = rangedCaster && OutOfPowerForOffense();
                if (rangedCaster && !outOfPowerMelee || !IsMeleeChasing(Body.TargetObject))
                    Body.StopFollowing();
                if (!rangedCaster && Body.TargetObject is GameLiving meleeTarget && TryEngageUnderMeleePressure(meleeTarget))
                    return;

                // End our own staff swings/chase before considering a spell.
                // Otherwise each new swing refreshes SelfInterruptTime forever.
                // Actual incoming interruption still goes through native rules.
                // A caster with no power for any attack spell keeps swinging.
                if (rangedCaster && !Body.IsBeingInterruptedByOther && Body.attackComponent.AttackState &&
                    !OutOfPowerForOffense())
                    Body.StopAttack();

                if (!UsesDefensiveOnlyPet)
                {
                    // Primary caster-pet classes never hand ownership of the AI
                    // turn to the pet. The pet gets the same target while the
                    // owner continues immediately through its ordinary legal
                    // spell/range/melee decision below.
                    if (BotBody.CharacterClass != null &&
                        AutonomousPetSupport.UsesIndependentOwnerPetCombat((eCharacterClass)BotBody.CharacterClass.ID))
                        AutonomousPetSupport.SynchronizeIndependentPet(
                            Body,
                            Body.ControlledBrain,
                            ResolvePetCombatTarget(Body.TargetObject as GameLiving));
                    else
                        CommandPetAttack(Body.TargetObject as GameLiving);
                }

                if (protectionTarget != null && TryPriorityTaunt(protectionTarget))
                {
                    Body.StopAttack();
                    return;
                }

                bool bowCycleOwnsAction = Body.attackComponent != null &&
                    Body.rangeAttackComponent != null &&
                    BotRangedCombat.BowCycleOwnsAction(
                        (eCharacterClass)BotBody.CharacterClass.ID,
                        Body.attackComponent.AttackState,
                        Body.ActiveWeaponSlot,
                        Body.rangeAttackComponent.RangedAttackState);
                bool spellAction = !bowCycleOwnsAction && CheckSpells(eCheckSpellType.Offensive);
                bool hasPendingSpell = Body.castingComponent?.HasPendingSkillRequests == true;
                Spell pendingSpell = null;
                if (hasPendingSpell)
                    Body.castingComponent.TryPeekPendingSpell(out pendingSpell);
                bool continueSavageMelee = SavageBotCombatPolicy.ContinueMeleeAfterSpell(
                    (eCharacterClass)BotBody.CharacterClass.ID,
                    spellAction,
                    Body.IsCasting,
                    Body.castingComponent?.SpellHandler?.Spell,
                    hasPendingSpell,
                    pendingSpell);
                bool preserveRangedDraw = BotRangedCombat.PreserveDrawAfterInstantSpell(
                    spellAction,
                    Body.IsCasting,
                    Body.castingComponent?.HasPendingSkillRequests == true,
                    Body.ActiveWeaponSlot,
                    Body.rangeAttackComponent?.RangedAttackState ?? eRangedAttackState.None);
                bool continueMinstrelMelee = MinstrelBotCombatPolicy.ContinueAfterInstantDamage(
                    UsesMinstrelHybridCombat, spellAction, Body.IsCasting,
                    Body.castingComponent?.SpellHandler?.Spell, hasPendingSpell, pendingSpell);
                if (spellAction && !continueSavageMelee && !preserveRangedDraw && !continueMinstrelMelee)
                {
                    Body.StopAttack();
                }
                else
                {
                    if (rangedCaster && Body.TargetObject is GameLiving spellTarget)
                    {
                        // Try casts (including instant spells) first.
                        // A close incoming attack permits melee, and so does
                        // having no power for any attack spell: the caster walks
                        // in with its staff until it can cast again. Cooldowns,
                        // an already-active debuff or a rejected cast never
                        // order a staff charge.
                        if (TryEngageUnderMeleePressure(spellTarget)) return;
                        if (TryOutOfPowerMelee(spellTarget)) return;
                        ApproachForOffensiveSpell(spellTarget);
                        return;
                    }

                    CheckOffensiveAbilities();

                    if (Body.ControlledBrain != null && !UsesDefensiveOnlyPet)
                        CommandPetAttack(Body.TargetObject as GameLiving);

                    if (BotBody.CharacterClass.ClassType == eClassType.ListCaster && BotBody.CharacterClass.ID != (int)eCharacterClass.Valewalker)
                    {
                        if (Body.TargetObject is GameLiving casterTarget && ApproachForOffensiveSpell(casterTarget))
                            return;

                    }

                    // Switch weapon based on stance and target distance before attacking
                    if (Body.TargetObject is GameLiving livingTarget)
                    {
                        int distance = (int)Body.GetDistanceTo(livingTarget);
                        var stance = BotBody.Stance;
                        DbInventoryItem distanceItem = BotBody.Inventory.GetItem(eInventorySlot.DistanceWeapon);
                        bool hasRangedCombatWeapon = BotRangedCombat.CanUse(BotBody, distanceItem) &&
                                                     Body.Endurance >= Body.rangeAttackComponent.ShotEnduranceCost;

                        // Direct melee pressure was handled above by
                        // TryEngageUnderMeleePressure.  Until that happens, a
                        // persistent Scout/Hunter/Ranger holds bow range even
                        // when its target closes inside nominal melee distance.
                        // Proximity alone must not restart a valid draw.
                        bool targetForcesMelee = distance <= Body.MeleeAttackRange &&
                                                 !BotRangedCombat.UsesAutonomousBowPositioning(BotBody) ||
                                                 ArcherMeleeFallbackActive;

                        bool useRanged = BotRangedCombat.ShouldUseRangedWeapon(
                            (eCharacterClass)BotBody.CharacterClass.ID,
                            stance,
                            targetForcesMelee,
                            hasRangedCombatWeapon);
                        if (useRanged)
                        {
                            if (Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance)
                                Body.SwitchWeapon(eActiveWeaponSlot.Distance);

                            int rangedAttackRange = Body.attackComponent.AttackRange;
                            if (BotRangedCombat.ShouldCloseToRangedRange(
                                    true, distance, rangedAttackRange))
                            {
                                int desired = BotRangedCombat.BowApproachDistance(Body.MeleeAttackRange, rangedAttackRange);
                                BotBody.WakeRecoveryRest();   // a resting bot ignores follow orders
                                Body.StopAttack();
                                Body.Follow(livingTarget,
                                    (short)Math.Clamp(desired, 160, short.MaxValue),
                                    (short)Math.Clamp((int)MAX_AGGRO_LIST_DISTANCE, 200, (int)short.MaxValue));
                                return;
                            }
                        }
                        else
                        {
                            // Includes the class-level Savage override. A real
                            // melee weapon makes StartAttack close to the target
                            // instead of entering the ranged draw state. Repair
                            // the GameBot loadout once before conceding that the
                            // actor has no legal melee action; never leave a
                            // stale "Fighting" target with no weapon.
                            bool weaponReady = SwitchToUsableMeleeWeapon();
                            if (!weaponReady)
                            {
                                BotBody.EnsureBotWeaponReady();
                                weaponReady = SwitchToUsableMeleeWeapon();
                            }
                            if (!weaponReady)
                            {
                                Body.StopAttack();
                                Body.TargetObject = null;
                                return;
                            }
                            QueueUsableMeleeStyle();
                        }
                    }

                    // Preserve a draw already in progress. Melee keeps its
                    // existing follow/retry behavior; a new target still starts.
                    if (Body.ActiveWeaponSlot != eActiveWeaponSlot.Distance ||
                        BotRangedCombat.NeedsAttackStart(Body.attackComponent.AttackState, previousAttackTarget, Body.TargetObject))
                        Body.StartAttack(Body.TargetObject);
                }
            }
            else
            {
                TryEnterStealthWhileIdle();
            }
        }

        private void TryEnterStealthWhileIdle()
        {
            // Idle is not a reason to hide (and slow travel). Stealth belongs to
            // the approach to a legal enemy player/bot, not random PvE pauses.
            if (Body.IsStealthed) Body.Stealth(false);
        }

        private bool TryApproachAmbush(GameLiving target)
        {
            if (!BotRvrAmbush.CanApproach(BotBody, target)) return false;
            int range = Body.MeleeAttackRange;
            if (BotRangedCombat.IsDedicatedArcher((eCharacterClass)BotBody.CharacterClass.ID) &&
                BotRangedCombat.CanUse(BotBody, BotBody.Inventory.GetItem(eInventorySlot.DistanceWeapon)))
                range = 1_000;
            if (Body.IsWithinRadius(target, range)) return false;
            Body.Stealth(true);
            if (!Body.IsStealthed) return false; // A running song can legally prevent stealth.
            Body.Follow(target, (short)range, (short)MAX_AGGRO_LIST_DISTANCE);
            return true;
        }

        private bool NeedsOffensiveSpellApplication(GameLiving target, Spell spell) =>
            spell != null && (spell.Duration == 0 || !LivingHasEffect(target, spell) ||
                spell.SpellType is eSpellType.DirectDamageWithDebuff or eSpellType.DamageSpeedDecrease);

        private int OffensiveApproachRange(GameLiving target)
        {
            int range = SpellApproachRange();
            // An Animist fights through its shrooms: stand where they reach the
            // target (1,000), not at nuke range (1,500), so every shroom can be
            // planted beside the Animist instead of out by the enemy.
            int shroomRange = BotAnimistPolicy.ShroomStandoffRange(BotBody);
            return shroomRange > 0 && range > 0 ? Math.Min(range, shroomRange) : range;
        }

        private int SpellApproachRange()
        {
            // SortSpells stores bolts and instant nukes outside HarmfulSpells.
            // Read the small learned list so those classes use their real range.
            return Body.Spells?.Where(spell => spell != null && spell.IsHarmful &&
                    spell.Level <= Body.Level && spell.Target is eSpellTarget.ENEMY or eSpellTarget.AREA or eSpellTarget.CONE &&
                    spell.SpellType is not eSpellType.Charm and not eSpellType.Amnesia and not eSpellType.Confusion and not eSpellType.Taunt &&
                    !AutonomousPetSupport.IsDisabledBotDamageShield(BotBody, spell) &&
                    AnimistSingleTargetPolicy.AllowsAutomatedSpell(BotBody, spell))
                .Select(spell => spell.Target == eSpellTarget.AREA && spell.Range <= 0
                    ? Math.Max(1, spell.Radius) : Math.Max(1, spell.CalculateEffectiveRange(Body)))
                .DefaultIfEmpty(0).Max() ?? 0;
        }

        private long _closerSpellApproachUntilTick;

        /// <summary>
        /// A watched pull that has not connected asks the caster to stand well
        /// inside its spell range for a while (a wandering target, awkward
        /// terrain, or a cast that kept failing at the edge of range).
        /// </summary>
        public void RequestCloserSpellApproach(long untilTick) => _closerSpellApproachUntilTick = untilTick;

        private long _archerMeleeFallbackUntilTick;

        /// <summary>
        /// A watched pull whose archer stalled close to its target (the native NPC
        /// attack treats ~300 units as melee distance for bows, while the bot keeps
        /// holding its bow there) fights it in melee for a while instead.
        /// </summary>
        public void RequestArcherMeleeFallback(long untilTick) => _archerMeleeFallbackUntilTick = untilTick;

        private bool ArcherMeleeFallbackActive => GameLoop.GameLoopTime < _archerMeleeFallbackUntilTick;

        private bool ApproachForOffensiveSpell(GameLiving target)
        {
            if (target?.IsAlive != true || Body.IsCrowdControlled ||
                !PrefersCurrentSpellRange())
                return false;

            int castRange = OffensiveApproachRange(target);
            if (castRange > 0 && GameLoop.GameLoopTime < _closerSpellApproachUntilTick)
                castRange = Math.Max(300, castRange * 6 / 10);
            if (castRange <= 0 || Body.IsWithinRadius(target, castRange))
                return false;

            int desired = Math.Max(160, castRange - 100);
            // Walking into range is an intentional combat action. A resting
            // bot ignores follow orders, so an out-of-range target would
            // otherwise leave it standing still indefinitely.
            BotBody.WakeRecoveryRest();
            Body.StopAttack();
            Body.Follow(target,
                (short)Math.Clamp(desired, 160, short.MaxValue),
                (short)Math.Clamp((int)MAX_AGGRO_LIST_DISTANCE, 200, (int)short.MaxValue));
            return true;
        }

        private bool TryEngageUnderMeleePressure(GameLiving target)
        {
            if (target?.IsAlive != true || !Body.IsWithinRadius(target, Body.MeleeAttackRange + 35))
                return false;

            // Being close to an enemy is not itself an interruption. A caster
            // may legally finish a spell while the selected mob has not yet
            // attacked. SelfInterruptTime is also raised by the bot's own
            // melee swings, so only an external hit/targeted attack should
            // force the melee fallback.
            // LastInterrupter retains the last actor indefinitely; identity is
            // not proof of a current hit once the interruption window expires.
            bool actuallyUnderAttack = Body.IsBeingInterruptedByOther ||
                target == _recentDirectAttacker &&
                GameLoop.GameLoopTime - _recentDirectAttackTick <= 5_000;
            if (!actuallyUnderAttack)
                return false;

            // A player cannot repeatedly begin an interruptible cast while a mob
            // is standing on them. Stop the failed cast loop and defend with the
            // class-appropriate melee weapon until range is opened again.
            if (Body.IsCasting)
                Body.StopCurrentSpellcast();

            StopTwistedSong();
            SwitchToUsableMeleeWeapon();

            CheckOffensiveAbilities();
            TryUseInstantOffenseWhileMeleeing(target);
            CommandPetAttack(target);

            QueueUsableMeleeStyle();

            if (!Body.attackComponent.AttackState)
                Body.StartAttack(target);
            return true;
        }

        /// <summary>
        /// True when the caster knows attack spells but cannot pay for any of them.
        /// A spell that costs nothing (or none known) never counts as out of power.
        /// </summary>
        public static bool OutOfPower(IEnumerable<int> attackSpellCosts, int mana)
        {
            bool any = false;
            foreach (int cost in attackSpellCosts)
            {
                if (cost <= mana)
                    return false;
                any = true;
            }
            return any;
        }

        private bool OutOfPowerForOffense()
        {
            if (Body.Spells == null || BotBody == null)
                return false;
            return OutOfPower(Body.Spells
                .Where(spell => spell != null && spell.IsHarmful && spell.Level <= Body.Level &&
                    spell.Target is eSpellTarget.ENEMY or eSpellTarget.AREA or eSpellTarget.CONE &&
                    spell.SpellType is not eSpellType.Charm and not eSpellType.Amnesia and
                        not eSpellType.Confusion and not eSpellType.Taunt &&
                    !AutonomousPetSupport.IsDisabledBotDamageShield(BotBody, spell) &&
                    !BotSpellPower.BlocksAttackerRotation(BotBody, spell) &&
                    !IsOutclassedDamageSpell(spell) &&
                    AnimistSingleTargetPolicy.AllowsAutomatedSpell(BotBody, spell))
                .Select(spell => BotBody.PowerCost(spell)), Body.Mana);
        }

        /// <summary>
        /// Bots always fight with their best damage ranks. A damage spell is skipped
        /// when a higher rank of the same kind is known, or when it is under half the
        /// level of the bot's best damage spell (the top rank of a barely trained
        /// line, e.g. a level 4 lifedrain on a level 50 Bonedancer, hits for 1). When
        /// the real spells cost more power than the bot has, it melees instead.
        /// </summary>
        public static bool IsOutclassedDamageSpell(Spell spell, IEnumerable<Spell> known, int casterLevel)
        {
            if (spell == null || !spell.IsHarmful || !BotCasterPriority.IsDamage(spell))
                return false;
            int best = 0;
            foreach (Spell other in known)
            {
                if (other == null || other == spell || !other.IsHarmful || other.Level > casterLevel ||
                    !BotCasterPriority.IsDamage(other))
                    continue;
                best = Math.Max(best, other.Level);
                if (other.Level > spell.Level && other.SpellType == spell.SpellType &&
                    other.Target == spell.Target && (other.Radius > 0) == (spell.Radius > 0) &&
                    (other.CastTime <= 0) == (spell.CastTime <= 0) && other.IsPBAoE == spell.IsPBAoE)
                    return true;
            }
            return best - spell.Level >= 5 && spell.Level * 2 < best;
        }

        private bool IsOutclassedDamageSpell(Spell spell) =>
            Body?.Spells != null && IsOutclassedDamageSpell(spell, Body.Spells, Body.Level);

        /// <summary>
        /// A caster (pet classes included) with no power for any attack spell walks
        /// in and fights with its staff alongside its pet, then casts again as soon
        /// as it can pay for a spell. A Necromancer's shade has no melee.
        /// </summary>
        private bool TryOutOfPowerMelee(GameLiving target)
        {
            if (target?.IsAlive != true || Body.IsCasting ||
                BotBody?.CharacterClass?.ID == (int)eCharacterClass.Necromancer ||
                !GameServer.ServerRules.IsAllowedToAttack(Body, target, true) ||
                !OutOfPowerForOffense())
                return false;

            StopTwistedSong();
            SwitchToUsableMeleeWeapon();
            CheckOffensiveAbilities();
            CommandPetAttack(target);
            QueueUsableMeleeStyle();
            // Re-issue the attack (which restarts the melee chase) when the target ran
            // off, e.g. after aggroing a distant party member.
            if (!Body.attackComponent.AttackState || Body.TargetObject != target ||
                !IsMeleeChasing(target) && !Body.IsWithinRadius(target, Body.MeleeAttackRange))
            {
                Body.TargetObject = target;
                Body.StartAttack(target);
            }
            return true;
        }

        private GameLiving RecentDirectAttacker()
        {
            GameLiving attacker = _recentDirectAttacker;
            if (attacker?.IsAlive != true || attacker.ObjectState != GameObject.eObjectState.Active ||
                attacker.CurrentRegion != Body.CurrentRegion ||
                GameLoop.GameLoopTime - _recentDirectAttackTick > 5_000 ||
                !Body.IsWithinRadius(attacker, MAX_AGGRO_LIST_DISTANCE) || !CanDefendAgainst(attacker))
            {
                _recentDirectAttacker = null;
                return null;
            }

            return attacker;
        }

        private void QueueUsableMeleeStyle()
        {
            if (Body.styleComponent.NextCombatStyle != null || Body.ActiveWeapon == null)
                return;
            Body.styleComponent.NextCombatStyle = BotMeleeStylePolicy.Select(BotBody,
                Body.attackComponent.attackAction.LastAttackData, false);
        }

        private bool SwitchToUsableMeleeWeapon()
        {
            if (BotBody?.CharacterClass?.ID == (int)eCharacterClass.Savage)
            {
                if (!SavageBotWeaponPolicy.TryBestActiveSlot(BotBody, out eActiveWeaponSlot savageSlot))
                    return false;
                if (Body.ActiveWeaponSlot != savageSlot)
                    Body.SwitchWeapon(savageSlot);
                return true;
            }

            DbInventoryItem rightHand = BotBody?.Inventory?.GetItem(eInventorySlot.RightHandWeapon);
            DbInventoryItem leftHand = BotBody?.Inventory?.GetItem(eInventorySlot.LeftHandWeapon);
            DbInventoryItem twoHand = BotBody?.Inventory?.GetItem(eInventorySlot.TwoHandWeapon);
            bool rightUsable = BotWeaponStats.FitsConfiguredSlot(BotBody, rightHand, eInventorySlot.RightHandWeapon);
            bool leftUsable = BotWeaponStats.CanUseMelee(BotBody, leftHand);
            bool twoHandUsable = BotWeaponStats.FitsConfiguredSlot(BotBody, twoHand, eInventorySlot.TwoHandWeapon);
            // The standard attack action requires a real primary-hand weapon.
            // An off-hand-only persisted loadout is not combat-ready; weapon
            // reconciliation will move/create a legal primary weapon instead.
            bool oneHandUsable = rightUsable;
            bool preferTwoHand = PreferTwoHandedSlot(twoHandUsable, oneHandUsable,
                BotBody.BotSpec?.Is2H == true ||
                BotBody.CharacterClass.ClassType == eClassType.ListCaster ||
                BotBody.CharacterClass.ID is (int)eCharacterClass.Friar or (int)eCharacterClass.Valewalker);

            if (preferTwoHand)
            {
                if (Body.ActiveWeaponSlot != eActiveWeaponSlot.TwoHanded)
                    Body.SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                return true;
            }

            if (oneHandUsable)
            {
                if (Body.ActiveWeaponSlot != eActiveWeaponSlot.Standard)
                    Body.SwitchWeapon(eActiveWeaponSlot.Standard);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The two-handed slot is used only when it holds a usable weapon. A two-handed build
        /// that has no usable two-hander yet (a level 1 Skald, a Paladin or Armsman before its
        /// two-handed ability) fights with whatever one-hander it has meanwhile.
        /// </summary>
        public static bool PreferTwoHandedSlot(bool twoHandUsable, bool oneHandUsable, bool twoHandedBuild) =>
            twoHandUsable && (!oneHandUsable || twoHandedBuild);

        private void TryUseInstantOffenseWhileMeleeing(GameLiving target)
        {
            if (target?.IsAlive != true || Body.InstantHarmfulSpells == null)
                return;

            Body.TargetObject = target;
            foreach (Spell spell in Body.InstantHarmfulSpells
                         .Where(spell => spell != null && spell.Level <= Body.Level)
                         .OrderByDescending(spell => spell.Level))
            {
                if (CheckInstantOffensiveSpells(spell))
                    break;
            }
        }

        private void HoldSupportCombat()
        {
            Body.StopAttack();
            if (!UsesDefensiveOnlyPet) Body.ControlledBrain?.Disengage();
            if (Body.IsCasting && Body.castingComponent?.SpellHandler?.Spell?.IsHarmful == true)
                Body.StopCurrentSpellcast();
        }

        private void PerformGroupSupport()
        {
            if (CompanionFollowPolicy.SendsDruidPet(BotBody))
                TryCommandCompanionDruidPet(CalculateNextAttackTarget());
            HoldSupportCombat();
            if (!CheckHeals() && !TryPvpCrowdControl()) CheckSpells(eCheckSpellType.Defensive);
            if (BotBody.IsPlayerLedGroup)
            {
                if (!Body.IsCasting && Body.castingComponent?.HasPendingSkillRequests != true)
                    FollowFormation();
                else
                    FollowDuringMobileSong();
            }
        }

        private void FollowDuringMobileSong()
        {
            if (BotBody?.IsPlayerLedGroup == true && !BotBody.IsRecoveryResting &&
                !BotBody.IsIncapacitated && !BotBody.IsOnStableMasterRoute &&
                BotSongTwistPolicy.HasMobileSongCast(Body))
                FollowFormation();
        }

        private void TryCommandCompanionDruidPet(GameLiving target)
        {
            if (!CompanionFollowPolicy.SendsDruidPet(BotBody) || target?.IsAlive != true ||
                !CanAggroTarget(target) || !CompanionEngagementMode.Allows(Body, target) ||
                !Body.IsWithinRadius(target, GROUP_DEFENSE_ASSIST_RADIUS) ||
                Body.ControlledBrain is not { Body: { IsAlive: true } } pet) return;
            // The Druid remains a support actor. Only its pet gets an attack
            // order, and an existing matching order is not restarted each tick.
            if (pet is ControlledMobBrain controlled && controlled.OrderedAttackTarget == target) return;
            pet.Attack(target);
        }

        private void FollowTravelingCompanionPerformer()
        {
            // Only player-led /spawn performers: ordinary gamebot route and
            // scheduling rules are untouched. Optional upkeep can otherwise
            // return before the FOLLOW state's mobile-song movement branch.
            if (BotBody is not { IsTemporaryGroupHelper: true, IsAutonomousWorldBot: false,
                    IsRecoveryResting: false, IsOnStableMasterRoute: false, IsAlive: true } bot ||
                !IsClassicSongClass(bot) || !bot.IsPlayerLedGroup || bot.IsIncapacitated ||
                bot.InCombat || HasAggro || bot.IsAttacking ||
                FSM.GetCurrentState()?.StateType != eFSMStateType.FOLLOW ||
                AssistedPlayer is not { IsAlive: true, IsMoving: true } leader ||
                leader.CurrentRegion != bot.CurrentRegion ||
                (bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true) &&
                    !BotSongTwistPolicy.HasMobileSongCast(bot))
                return;

            FollowFormation();
            _lastPerformerFollowTick = GameLoop.GameLoopTime;
        }

        private bool IsTankClass => BotPartyRoles.IsTank(BotBody);

        public bool TryAssistAutonomousPveCombat()
        {
            if (BotBody?.IsAutonomousWorldBot != true || BotBody.IsPlayerLedGroup ||
                BotBody.IsTemporaryGroupHelper || Body.Group == null)
                return false;

            // Use the party's actual combat targets, not a new camp search.
            // The controller must then yield to the normal combat FSM so support,
            // pet attacks and movement into spell/melee range can execute.
            GameLiving target = Body.Group.GetMembersInTheGroup()
                .Where(member => member != Body && member?.IsAlive == true &&
                    member.CurrentRegion == Body.CurrentRegion &&
                    (member.InCombat || member.IsAttacking))
                .Select(member => member.TargetObject as GameLiving)
                .Where(candidate => CanDefendAgainst(candidate) &&
                    Body.IsWithinRadius(candidate, GROUP_DEFENSE_ASSIST_RADIUS))
                .OrderBy(candidate => Body.GetDistanceTo(candidate))
                .FirstOrDefault();
            if (target == null)
                return false;

            AddToAggroList(target, 1);
            FSM.SetCurrentState(eFSMStateType.AGGRO);
            return HasAggro;
        }

        private GameLiving FindProtectionTarget()
        {
            if (!IsTankClass || Body?.Group == null)
                return null;

            GamePlayer leader = AssistedPlayer;
            HashSet<GameLiving> protectedMembers = Body.Group.GetMembersInTheGroup()
                .Where(member => member?.IsAlive == true && member.CurrentRegionID == Body.CurrentRegionID &&
                                 Body.IsWithinRadius(member, GROUP_DEFENSE_ASSIST_RADIUS))
                .ToHashSet();
            if (protectedMembers.Count == 0)
                return null;

            IEnumerable<GameLiving> candidates = AggroList.Keys
                .Concat(Body.GetNPCsInRadius(2600).Cast<GameLiving>())
                .Concat(Body.GetPlayersInRadius(2600).Cast<GameLiving>())
                .Distinct();
            return candidates
                .Where(candidate => candidate?.IsAlive == true &&
                                    candidate.TargetObject is GameLiving victim &&
                                    protectedMembers.Contains(victim) &&
                                    CanAggroTarget(candidate))
                .OrderByDescending(candidate => candidate.TargetObject == leader)
                .ThenByDescending(candidate => candidate.TargetObject is GameBot bot && bot.Brain is BotBrain { IsHealer: true })
                .ThenBy(candidate => Body.GetDistanceTo(candidate))
                .FirstOrDefault();
        }

        private bool TryPriorityTaunt(GameLiving target)
        {
            if (!IsTankClass || target?.TargetObject is not GameLiving victim || Body.Group == null ||
                victim.Group != Body.Group || !Body.Group.IsInTheGroup(victim) ||
                victim.CurrentRegionID != Body.CurrentRegionID ||
                !Body.IsWithinRadius(victim, GROUP_DEFENSE_ASSIST_RADIUS))
                return false;

            if (GameLoop.GameLoopTime >= _nextPriorityTauntTick)
            {
                // Instant taunts (no cast time, like the Sluaghbinder's) are
                // sorted into InstantHarmfulSpells, so look in both lists.
                Spell taunt = (Body.HarmfulSpells ?? Enumerable.Empty<Spell>())
                    .Concat(Body.InstantHarmfulSpells ?? Enumerable.Empty<Spell>())
                    .Where(spell => spell.SpellType == eSpellType.Taunt &&
                                    Body.GetSkillDisabledDuration(spell) <= 0 &&
                                    Body.Mana >= BotBody.PowerCost(spell) &&
                                    Body.IsWithinRadius(target, Math.Max(500, spell.Range)))
                    .OrderByDescending(spell => spell.Level)
                    .FirstOrDefault();
                if (taunt != null)
                {
                    _nextPriorityTauntTick = GameLoop.GameLoopTime + 2500;
                    return Body.CastSpell(taunt, m_mobSpellLine);
                }
            }

            Style tauntStyle = BotBody.StylesTaunt?
                .Where(style => style.Level <= Body.Level)
                .OrderByDescending(style => style.Level)
                .FirstOrDefault();
            if (tauntStyle != null)
            {
                Body.styleComponent.NextCombatStyle = tauntStyle;
                Body.styleComponent.NextCombatBackupStyle = BotBody.StylesAnytime?
                    .Where(style => style.Level <= Body.Level)
                    .OrderByDescending(style => style.Level)
                    .FirstOrDefault();
            }

            return false;
        }

        public void CheckOffensiveAbilities()
        {
            if (Body.Abilities == null || Body.Abilities.Count <= 0)
                return;

            foreach (Ability ab in Body.GetAllAbilities())
            {
                if (Body.GetSkillDisabledDuration(ab) == 0)
                {
                    switch (ab.KeyName)
                    {
                        case Abilities.Berserk:
                        {
                            if (Body.TargetObject is GameLiving target)
                            {
                                if (Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange) &&
                                    GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                                {
                                    var berserk = new BerserkECSGameEffect(
                                        new ECSGameEffectInitParams(Body, BerserkAbilityHandler.DURATION, 1));

                                    // Constructing an ECS effect does not activate it. Only consume
                                    // the reuse timer after the frenzy was accepted by the effect
                                    // service, otherwise bots silently waste Berserk every seven minutes.
                                    if (berserk.Start())
                                        Body.DisableSkill(ab, 420000);
                                }
                            }
                            break;
                        }

                        case Abilities.Stag:
                        {
                            if (Body.TargetObject is GameLiving target)
                            {
                                if (Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange) &&
                                    GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                                {
                                    new StagECSGameEffect(new ECSGameEffectInitParams(Body, StagAbilityHandler.DURATION * 1000, 1), ab.Level);
                                    Body.DisableSkill(ab, StagAbilityHandler.DURATION * 5000);
                                }
                            }
                            break;
                        }
                    }
                }
            }
        }

        #endregion

        #region Spells

        public enum eCheckSpellType
        {
            Offensive,
            Defensive,
            CrowdControl
        }

        protected static SpellLine m_mobSpellLine = SkillBase.GetSpellLine(GlobalSpellsLines.Mob_Spells);

        public virtual bool CheckSpells(eCheckSpellType type)
        {
            if (Body == null || Body.Spells == null || Body.Spells.Count <= 0)
                return false;

            // CastSpell starts an asynchronous cast/animation. Re-entering
            // this selector on every AI pulse while that cast is still active
            // makes Wizards, Necromancers and other ranged casters repeatedly
            // select/interrupt their own spell. Treat an active cast as the
            // completed decision for this pulse and wait for the native cast
            // pipeline to finish.
            if (Body.IsCasting || Body.castingComponent?.HasPendingSkillRequests == true)
                return true;

            // Necromancer commands finish on the shade first, then the servant
            // performs the real cast. Do not enqueue another command while the
            // servant is still casting or has a queued spell; that produced the
            // visible rapid-fire loop and repeatedly replaced pending actions.
            if (Body.ControlledBrain is NecromancerPetBrain necromancerPetBrain &&
                necromancerPetBrain.HasPendingBotCommand)
                return true;

            bool casted = false;
            List<Spell> spellsToCast = new();

            if (CheckHeals())
                return true;

            // A classic Necromancer does not cast its servant attacks directly.
            // The shade issues a PetSpell wrapper and the native
            // NecromancerPetBrain queues/executes its SubSpellID on the zombie.
            // Most wrappers are instant and therefore never entered the generic
            // interruptible-spell selector below; handle that command pipeline
            // explicitly while retaining all native cost, queue, range and
            // servant-cast behavior.
            if (TryIssueNecromancerServantCommand(type))
                return true;

            if (!casted && type == eCheckSpellType.Defensive)
            {
                if (Body.CanCastMiscSpells)
                    casted = CheckDefensiveSpells(Body.MiscSpells);
            }
            else if (!casted && type == eCheckSpellType.Offensive)
            {
                if (TryPvpCrowdControl()) return true;
                if (TryBardPveAddMez()) return true;
                if (TryShamanPveAddRoot()) return true;
                if (TryShortCombatShield()) return true;
                if (BotBody.CharacterClass.ID == (int)eCharacterClass.Cleric)
                {
                    if (!Util.Chance(Math.Max(5, Body.ManaPercent - 50)))
                        return false;
                }

                // Check instant spells
                if (Body.CanCastInstantHarmfulSpells)
                {
                    IEnumerable<Spell> instantOffense = Body.InstantHarmfulSpells;
                    if (UsesMinstrelHybridCombat)
                        instantOffense = instantOffense.OrderByDescending(BotCasterPriority.IsDamage);
                    foreach (Spell spell in instantOffense)
                    {
                        bool meleePressure = Body.IsBeingInterruptedByOther &&
                            Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange + 35);
                        if (!BotCasterPriority.AllowInstant(spell, PrefersCurrentSpellRange(), meleePressure))
                            continue;
                        if (CheckInstantOffensiveSpells(spell))
                            return true;
                    }
                }

                if (Body.CanCastInstantMiscSpells)
                {
                    IEnumerable<Spell> instantMisc = Body.InstantMiscSpells;
                    if (BotBody.CharacterClass.ID == (int)eCharacterClass.Savage)
                        instantMisc = instantMisc.OrderBy(spell => SavageBotCombatPolicy.BuffPriority(spell.SpellType));
                    foreach (Spell spell in instantMisc)
                    {
                        if (CheckInstantDefensiveSpells(spell))
                            return true;
                    }
                }

                // Autonomous Minstrels try available instants first, then close
                // with their real weapon. No spell, cooldown, or low power must
                // leave a level-1 (or higher) Minstrel waiting at spell range.
                if (UsesMinstrelHybridCombat)
                    return false;

                // Nightshade melee focus
                if (BotBody.CharacterClass.ID == (int)eCharacterClass.Nightshade)
                    return false;

                // Melee hybrids prefer melee when in range
                if (!PrefersCurrentSpellRange() && (BotBody.CanUsePositionalStyles || BotBody.CanUseAnytimeStyles) && (Body.IsWithinRadius(Body.TargetObject, 550) || Body.ManaPercent <= 10))
                    return false;

                if (BotBody.CanCastCrowdControlSpells)
                {
                    int ccChance = 50;

                    GameLiving livingTarget = Body.TargetObject as GameLiving;

                    if (livingTarget?.TargetObject == Body && Body.IsWithinRadius(Body.TargetObject, 500))
                        ccChance = 95;

                    if ((!PrefersCurrentSpellRange() || Body.IsBeingInterruptedByOther) && Util.Chance(ccChance))
                    {
                        foreach (Spell spell in BotBody.CrowdControlSpells)
                        {
                            // Never mesmerize the monster this bot is trying to kill:
                            // the next hit breaks it, so the cast only delays the fight
                            // (solo level 1 Healers spent turns mezzing their own
                            // target). PvP control and Bard add-mez use separate paths;
                            // stuns stay allowed since they give free hits.
                            if (Body.TargetObject is GameNPC and not GameBot &&
                                spell.SpellType is eSpellType.Mesmerize or eSpellType.Mez)
                                continue;
                            if (CanCastOffensiveSpell(spell) && !LivingHasEffect((GameLiving)Body.TargetObject, spell))
                                spellsToCast.Add(spell);
                        }
                    }
                }

                if (BotBody.CanCastBolts && spellsToCast.Count < 1)
                {
                    foreach (Spell spell in BotBody.BoltSpells)
                    {
                        if (CanCastOffensiveSpell(spell))
                            spellsToCast.Add(spell);
                    }
                }

                if (spellsToCast.Count < 1)
                {
                    if (Body.CanCastHarmfulSpells)
                    {
                        foreach (Spell spell in Body.HarmfulSpells)
                        {
                            if (spell.SpellType == eSpellType.Charm ||
                                spell.SpellType == eSpellType.Amnesia ||
                                spell.SpellType == eSpellType.Confusion ||
                                spell.SpellType == eSpellType.Taunt)
                                continue;

                            if (CanCastOffensiveSpell(spell))
                                spellsToCast.Add(spell);
                        }
                    }
                }

                if (spellsToCast.Count > 0)
                {
                    // Do not roll a DoT/debuff already on this mob and mistake
                    // its refusal for a reason to abandon ranged combat.
                    spellsToCast.RemoveAll(spell => !NeedsOffensiveSpellApplication((GameLiving)Body.TargetObject, spell));
                    if (spellsToCast.Count == 0) return Body.IsCasting;
                    if (PrefersCurrentSpellRange() &&
                        spellsToCast.Exists(spell => spell.Range > Body.MeleeAttackRange))
                        spellsToCast.RemoveAll(spell => spell.Range <= Body.MeleeAttackRange);
                    if (PrefersCurrentSpellRange() && spellsToCast.Exists(BotCasterPriority.IsDamage))
                        spellsToCast.RemoveAll(spell => !BotCasterPriority.IsDamage(spell));
                    bool shamanHybrid = ShamanBotCombatPolicy.IsHybrid((eCharacterClass)BotBody.CharacterClass.ID);
                    Spell spellToCast = shamanHybrid
                        ? spellsToCast.OrderBy(ShamanBotCombatPolicy.RotationPriority)
                            .ThenByDescending(spell => spell.Level).First()
                        : BotBody.IsEndgameCompanion
                        ? TemporaryCompanionBalance.HighestSpell(spellsToCast)
                        : spellsToCast[Util.Random(spellsToCast.Count - 1)];

                    if (spellToCast.Uninterruptible || !Body.IsBeingInterrupted)
                        casted = CheckOffensiveSpells(spellToCast);
                    else if (shamanHybrid && ShamanBotCombatPolicy.HoldMeleeForCast(true,
                                 Body.IsBeingInterruptedByOther, Body.IsBeingSelfInterrupted))
                    {
                        // A ready nuke or an expired damage over time ends the
                        // melee: stop swinging so the Shaman's own swing timer
                        // runs out and the cast starts on a following turn.
                        Body.StopAttack();
                        return true;
                    }
                    // Bots never use Quickcast. Instants remain available above;
                    // an interrupted ordinary cast waits for its legal window.
                }
            }

            return casted || Body.IsCasting;
        }

        private bool TryIssueNecromancerServantCommand(eCheckSpellType type)
        {
            if (BotBody?.CharacterClass?.ID != (int)eCharacterClass.Necromancer ||
                type != eCheckSpellType.Offensive ||
                Body.ControlledBrain is not NecromancerPetBrain servantBrain ||
                servantBrain.Body?.IsAlive != true ||
                servantBrain.Body.ObjectState is not GameObject.eObjectState.Active ||
                GameLoop.GameLoopTime < _nextNecromancerCommandTick ||
                Body.IsCasting || servantBrain.HasPendingBotCommand)
                return false;

            GameLiving target;
            IEnumerable<Spell> commands = Body.Spells
                .Where(spell => spell != null && spell.SpellType == eSpellType.PetSpell &&
                                spell.SubSpellID > 0 && spell.Level <= Body.Level &&
                                Body.GetSkillDisabledDuration(spell) <= 0 &&
                                Body.Mana >= BotBody.PowerCost(spell));

            target = Body.TargetObject as GameLiving;
            if (target?.IsAlive != true || !GameServer.ServerRules.IsAllowedToAttack(Body, target, true))
                return false;

            // Helpful servant commands are exclusively maintained by
            // AutonomousPetSupport. Keeping them out of this combat selector
            // prevents a second cooldown/queue from recasting buffs or treating
            // one-shot power transfer as idle upkeep.
            commands = commands.Where(spell => spell.IsHarmful &&
                Body.IsWithinRadius(target, Math.Max(1, spell.CalculateEffectiveRange(Body))) &&
                ServantPayloadNeedsApplication(target, spell));

            // The normal spell construction already reduced each line to its
            // learned, level-valid ranks. Prefer the highest legal commands but
            // retain some variation between equally useful servant actions.
            Spell[] best = commands
                .OrderByDescending(spell => spell.Level)
                .ThenByDescending(spell => spell.SubSpellID)
                .Take(4)
                .ToArray();
            if (best.Length == 0)
                return false;

            Spell command = BotBody.IsEndgameCompanion
                ? TemporaryCompanionBalance.HighestSpell(best)
                : best[Random.Shared.Next(best.Length)];
            GameObject oldTarget = Body.TargetObject;
            Body.TargetObject = target;
            bool cast = Body.CastSpell(command, ResolveKnownSpellLine(command), false);
            Body.TargetObject = oldTarget;
            _nextNecromancerCommandTick = GameLoop.GameLoopTime + (cast ? 750 : 2_000);
            return cast;

            bool ServantPayloadNeedsApplication(GameLiving commandTarget, Spell wrapper)
            {
                Spell payload = SkillBase.GetSpellByID(wrapper.SubSpellID);
                if (payload == null)
                    return false;

                if (payload.IsHealing)
                    return commandTarget.HealthPercent < 88;

                return payload.Duration <= 0 || !LivingHasEffect(commandTarget, payload);
            }
        }

        private SpellLine ResolveKnownSpellLine(Spell spell)
        {
            if (spell == null)
                return m_mobSpellLine;
            if (_knownSpellLines.TryGetValue(spell.ID, out SpellLine cached))
                return cached;

            foreach (var tuple in BotBody.GetAllUsableListSpells())
            {
                if (tuple?.Item1 == null || tuple.Item2 == null ||
                    !tuple.Item2.OfType<Spell>().Any(known => known.ID == spell.ID))
                    continue;
                _knownSpellLines[spell.ID] = tuple.Item1;
                return tuple.Item1;
            }

            return m_mobSpellLine;
        }

        protected bool CanCastOffensiveSpell(Spell spell)
        {
            if (spell == null || spell.Level > Body.Level || IsOutclassedDamageSpell(spell) ||
                Body.TargetObject is not GameLiving target || !target.IsAlive ||
                !BardBotCrowdControlPolicy.AllowsOrdinaryOffense((eCharacterClass)BotBody.CharacterClass.ID, spell.SpellType) ||
                !ShamanBotCombatPolicy.AllowsOrdinaryOffense((eCharacterClass)BotBody.CharacterClass.ID, spell) ||
                BotSpellPower.BlocksAttackerRotation(BotBody, spell) ||
                !NeedsOffensiveSpellApplication(target, spell) ||
                Body.GetSkillDisabledDuration(spell) > 0 || Body.Mana < BotBody.PowerCost(spell))
                return false;

            if (spell.CastTime > 0 && spell.Target is eSpellTarget.ENEMY or eSpellTarget.AREA or eSpellTarget.CONE)
            {
                int range = spell.Target == eSpellTarget.AREA && spell.Range <= 0
                    ? Math.Max(1, spell.Radius)
                    : Math.Max(1, spell.CalculateEffectiveRange(Body));
                return Body.IsWithinRadius(target, range);
            }

            return false;
        }

        protected bool CanCastDefensiveSpell(Spell spell)
        {
            if (CompanionFollowPolicy.DeferBuff(BotBody, spell)) return false;
            if (spell == null || spell.IsHarmful || spell.Level > Body.Level ||
                !BotBody.CanAffordConcentration(spell) ||
                BotSongTwistPolicy.IsReservedPulse(BotBody, spell) ||
                Body.Mana < BotBody.PowerCost(spell))
                return false;

            if (spell.CastTime > 0 && Body.IsBeingInterrupted && !spell.Uninterruptible)
                return false;

            if (Body.GetSkillDisabledDuration(spell) > 0)
                return false;

            return true;
        }

        protected virtual bool CheckOffensiveSpells(Spell spell)
        {
            if (spell == null || Body.Mana < BotBody.PowerCost(spell))
                return false;

            if (spell.NeedInstrument && !TryEquipRealInstrument(BotBody, spell.InstrumentRequirement))
                return false;

            bool casted = false;

            if (Body.TargetObject is GameLiving living && NeedsOffensiveSpellApplication(living, spell))
            {
                casted = Body.CastSpell(spell, m_mobSpellLine);
            }

            return casted;
        }

        protected virtual bool CheckInstantDefensiveSpells(Spell spell)
        {
            if (CompanionFollowPolicy.DeferBuff(BotBody, spell)) return false;
            if (spell == null || spell.Level > Body.Level || Body.Mana < BotBody.PowerCost(spell) ||
                !BotBody.CanAffordConcentration(spell) ||
                BotSongTwistPolicy.IsReservedPulse(BotBody, spell) ||
                spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0)
                return false;

            bool castSpell = false;

            switch (spell.SpellType)
            {
                case eSpellType.SavageEnduranceHeal:
                    castSpell = SavageBotCombatPolicy.ShouldUseEnduranceHeal(
                        Body.HealthPercent, Body.EndurancePercent);
                    break;

                case eSpellType.SavageCrushResistanceBuff:
                case eSpellType.SavageSlashResistanceBuff:
                case eSpellType.SavageThrustResistanceBuff:
                case eSpellType.SavageCombatSpeedBuff:
                case eSpellType.SavageDPSBuff:
                case eSpellType.SavageParryBuff:
                case eSpellType.SavageEvadeBuff:
                    int activeSavageBuffs = Body.effectListComponent.GetEffects(eEffect.SavageBuff)
                        .Count(effect => effect?.SpellHandler?.Spell != null);
                    // Health-cost buffs only once the Savage is actually at its
                    // target, never while still walking up (a level 1 Savage
                    // arrived half dead), and just one of them below level 10.
                    bool atTarget = Body.TargetObject is GameLiving buffTarget && buffTarget.IsAlive &&
                        Body.IsWithinRadius(buffTarget, Body.MeleeAttackRange + 150);
                    if (atTarget &&
                        SavageBotCombatPolicy.ShouldUseBuff(spell.SpellType, Body.HealthPercent, activeSavageBuffs, Body.Level,
                            ((GameLiving)Body.TargetObject).HealthPercent) &&
                        !LivingHasEffect(Body, spell))
                        castSpell = true;
                    break;

                case eSpellType.SummonHunterPet:
                    // Summons have an effect on the pet, not the caster. An
                    // owner-effect check incorrectly created a new pet every turn.
                    castSpell = Body.ControlledBrain == null;
                    break;

                case eSpellType.CombatHeal:
                case eSpellType.DamageAdd:
                case eSpellType.PaladinArmorFactorBuff:
                case eSpellType.DexterityQuicknessBuff:
                case eSpellType.EnduranceRegenBuff:
                case eSpellType.CombatSpeedBuff:
                case eSpellType.AblativeArmor:
                case eSpellType.Bladeturn:
                case eSpellType.OffensiveProc:
                    if (spell.SpellType == eSpellType.CombatSpeedBuff)
                    {
                        if (Body.TargetObject != null && !Body.IsWithinRadius(Body.TargetObject, Body.MeleeAttackRange))
                            break;
                    }

                    if (!LivingHasEffect(Body, spell))
                        castSpell = true;
                    break;
            }

            return castSpell && Body.CastSpell(spell, m_mobSpellLine);
        }

        protected virtual bool CheckInstantOffensiveSpells(Spell spell)
        {
            if (spell == null || IsOutclassedDamageSpell(spell) || Body.Mana < BotBody.PowerCost(spell) ||
                !BardBotCrowdControlPolicy.AllowsOrdinaryOffense((eCharacterClass)BotBody.CharacterClass.ID, spell.SpellType) ||
                spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0)
                return false;

            bool castSpell = false;

            switch (spell.SpellType)
            {
                case eSpellType.DirectDamage:
                case eSpellType.NightshadeNuke:
                case eSpellType.Lifedrain:
                case eSpellType.DexterityDebuff:
                case eSpellType.DexterityQuicknessDebuff:
                case eSpellType.StrengthDebuff:
                case eSpellType.StrengthConstitutionDebuff:
                case eSpellType.CombatSpeedDebuff:
                case eSpellType.DamageOverTime:
                case eSpellType.MeleeDamageDebuff:
                case eSpellType.AllStatsPercentDebuff:
                case eSpellType.CrushSlashThrustDebuff:
                case eSpellType.EffectivenessDebuff:
                case eSpellType.Disease:
                case eSpellType.Stun:
                case eSpellType.Mez:
                case eSpellType.Mesmerize:
                    if (spell.IsPBAoE && !Body.IsWithinRadius(Body.TargetObject, spell.Radius))
                        break;

                    if (!LivingHasEffect((GameLiving)Body.TargetObject, spell) && Body.IsWithinRadius(Body.TargetObject, spell.Range))
                        castSpell = true;
                    break;
            }

            ECSGameEffect pulseEffect = EffectListService.GetPulseEffectOnTarget(Body, spell);

            if (pulseEffect != null)
                return false;

            if (castSpell)
            {
                return Body.CastSpell(spell, m_mobSpellLine);
            }

            return false;
        }

        private bool IsMaintenanceTraveling() => BotBody is { } bot &&
            (bot.IsMoving || bot.IsReturningAfterRelease || bot.Group?.LivingLeader?.IsMoving == true ||
             bot.PersistentRecord?.Activity?.Contains("travel", StringComparison.OrdinalIgnoreCase) == true ||
             bot.PersistentRecord?.Activity?.Contains("walking", StringComparison.OrdinalIgnoreCase) == true);

        protected bool CheckDefensiveSpells(Spell spell)
        {
            if (spell.SpellType == eSpellType.SpeedEnhancement && spell.IsPulsing && !IsMaintenanceTraveling())
                return false;
            if (!CanCastDefensiveSpell(spell))
                return false;

            bool casted = false;

            Body.TargetObject = null;

            if (spell.NeedInstrument)
                return false;

            switch (spell.SpellType)
            {
                #region Summon

                case eSpellType.SummonCommander:
                case eSpellType.SummonUnderhill:
                case eSpellType.SummonDruidPet:
                case eSpellType.SummonSimulacrum:
                case eSpellType.SummonSpiritFighter:
                    if (Body.ControlledBrain != null)
                        return false;
                    Body.TargetObject = Body;
                    break;

                case eSpellType.SummonMinion:
                    if (Body.ControlledBrain != null)
                    {
                        IControlledBrain[] icb = Body.ControlledBrain.Body?.ControlledNpcList;
                        // A level-one Bonedancer Returned Commander has no
                        // subordinate array. Treat that as unavailable rather
                        // than aborting the bot's entire think turn. The
                        // autonomous pet policy replaces the commander when a
                        // valid rank becomes known.
                        if (icb == null || icb.Length == 0)
                            break;

                        int numberofpets = 0;

                        for (int i = 0; i < icb.Length; i++)
                        {
                            if (icb[i] != null)
                                numberofpets++;
                        }

                        if (numberofpets >= icb.Length)
                            break;

                        Body.TargetObject = Body;
                    }
                    break;

                #endregion

                #region Buffs

                case eSpellType.SpeedEnhancement when spell.IsPulsing:
                    if (!LivingHasEffect(Body, spell))
                        Body.TargetObject = Body;
                    break;

                case eSpellType.BodySpiritEnergyBuff:
                case eSpellType.HeatColdMatterBuff:
                case eSpellType.SpiritResistBuff:
                case eSpellType.EnergyResistBuff:
                case eSpellType.HeatResistBuff:
                case eSpellType.ColdResistBuff:
                case eSpellType.BodyResistBuff:
                case eSpellType.MatterResistBuff:
                case eSpellType.AllMagicResistBuff:
                case eSpellType.EnduranceRegenBuff:
                case eSpellType.PowerRegenBuff:
                case eSpellType.AblativeArmor:
                case eSpellType.AcuityBuff:
                case eSpellType.AFHitsBuff:
                case eSpellType.ArmorAbsorptionBuff:
                case eSpellType.BaseArmorFactorBuff:
                case eSpellType.SpecArmorFactorBuff:
                case eSpellType.PaladinArmorFactorBuff:
                case eSpellType.Buff:
                case eSpellType.CelerityBuff:
                case eSpellType.ConstitutionBuff:
                case eSpellType.CourageBuff:
                case eSpellType.CrushSlashTrustBuff:
                case eSpellType.DexterityBuff:
                case eSpellType.DexterityQuicknessBuff:
                case eSpellType.EffectivenessBuff:
                case eSpellType.FatigueConsumptionBuff:
                case eSpellType.FlexibleSkillBuff:
                case eSpellType.HasteBuff:
                case eSpellType.HealthRegenBuff:
                case eSpellType.HeroismBuff:
                case eSpellType.KeepDamageBuff:
                case eSpellType.MagicResistBuff:
                case eSpellType.MeleeDamageBuff:
                case eSpellType.MLABSBuff:
                case eSpellType.ParryBuff:
                case eSpellType.PowerHealthEnduranceRegenBuff:
                case eSpellType.StrengthBuff:
                case eSpellType.StrengthConstitutionBuff:
                case eSpellType.SuperiorCourageBuff:
                case eSpellType.ToHitBuff:
                case eSpellType.WeaponSkillBuff:
                case eSpellType.DamageAdd:
                case eSpellType.OffensiveProc:
                case eSpellType.DefensiveProc:
                case eSpellType.DamageShield:
                case eSpellType.SpeedEnhancement when spell.Target == eSpellTarget.PET:
                case eSpellType.CombatSpeedBuff when spell.Duration > 20:
                case eSpellType.CombatSpeedBuff when spell.IsConcentration:
                case eSpellType.MesmerizeDurationBuff when !spell.IsPulsing:
                case eSpellType.Bladeturn when !spell.IsPulsing:
                {
                    if (!BotBody.CanAffordConcentration(spell))
                        break;

                    if (spell.Target == eSpellTarget.REALM && !spell.IsPulsing)
                    {
                        Body.TargetObject = FindRandomMissingRealmBuffTarget(spell);
                        break;
                    }

                    if (spell.Target == eSpellTarget.PET)
                    {
                        if (spell.SpellType == eSpellType.DamageShield)
                            return false;

                        if (Body.ControlledBrain?.Body != null)
                        {
                            if (!LivingHasEffect(Body.ControlledBrain.Body, spell))
                                Body.TargetObject = Body.ControlledBrain.Body;
                        }

                        break;
                    }

                    if (!LivingHasEffect(Body, spell))
                    {
                        Body.TargetObject = Body;
                        break;
                    }

                    if (Body.Group != null)
                    {
                        if (spell.Target == eSpellTarget.REALM || spell.Target == eSpellTarget.GROUP)
                        {
                            foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                            {
                                if (groupMember != Body)
                                {
                                    if (!LivingHasEffect(groupMember, spell) && Body.IsWithinRadius(groupMember, spell.Range) && groupMember.IsAlive)
                                    {
                                        Body.TargetObject = groupMember;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (Body.TargetObject == null)
                        Body.TargetObject = FindMissingPartyPetBuffTarget(spell);
                }
                break;

                #endregion

                #region Cures

                case eSpellType.CureDisease:
                    if (Body.IsDiseased)
                    {
                        Body.TargetObject = Body;
                        break;
                    }

                    if (Body.Group != null)
                    {
                        foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        {
                            if (groupMember != Body && groupMember.IsDiseased && Body.IsWithinRadius(groupMember, spell.Range))
                            {
                                Body.TargetObject = groupMember;
                                break;
                            }
                        }
                    }
                    break;

                case eSpellType.CurePoison:
                    if (Body.IsPoisoned)
                    {
                        Body.TargetObject = Body;
                        break;
                    }

                    if (Body.Group != null)
                    {
                        foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        {
                            if (groupMember != Body && groupMember.IsPoisoned && Body.IsWithinRadius(groupMember, spell.Range))
                            {
                                Body.TargetObject = groupMember;
                                break;
                            }
                        }
                    }
                    break;

                case eSpellType.CureMezz:
                    if (Body.Group != null)
                    {
                        foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        {
                            if (groupMember != Body && groupMember.IsMezzed && Body.IsWithinRadius(groupMember, spell.Range))
                            {
                                Body.TargetObject = groupMember;
                                break;
                            }
                        }
                    }
                    break;

                #endregion
            }

            if (Body?.TargetObject != null)
            {
                casted = CastCoordinatedBuff(spell);
            }

            return casted;
        }

        bool CheckDefensiveSpells(List<Spell> spells)
        {
            List<(Spell, GameLiving)> spellsToCast = new(spells.Count);

            foreach (Spell spell in spells)
            {
                if (Body.InCombat && IsMaintainableClassBuff(spell))
                    continue;
                // Cast in the fight by TryShortCombatShield, never as upkeep.
                if (IsShortCombatShield(spell))
                    continue;
                // Caster-pet classes, including a Bonedancer's commander tree,
                // maintain their controlled pets through AutonomousPetSupport. The
                // generic defensive selector historically treated PET spells
                // as self buffs, queued the cast, then lost the AI turn when
                // the native target validation rejected it.  Repeating that on
                // every FOLLOW pulse starved formation movement forever after
                // the first successful summon.
                if (BotBody?.CharacterClass != null &&
                    AutonomousPetSupport.OwnsPetUpkeep(
                        (eCharacterClass)BotBody.CharacterClass.ID) &&
                    spell.Target is eSpellTarget.PET or eSpellTarget.CONTROLLED)
                {
                    continue;
                }
                if (CanCastDefensiveSpell(spell, out GameLiving target))
                    spellsToCast.Add((spell, target));
            }

            if (spellsToCast.Count == 0)
                return false;

            GameObject oldTarget = Body.TargetObject;
            (Spell spell, GameLiving target) spellToCast = BotBody.IsEndgameCompanion
                ? spellsToCast.OrderByDescending(entry => entry.Item1.Level).ThenByDescending(entry => entry.Item1.ID).First()
                : spellsToCast[Util.Random(spellsToCast.Count - 1)];
            Body.TargetObject = spellToCast.target;
            bool cast = CastCoordinatedBuff(spellToCast.spell);

            Body.TargetObject = oldTarget;
            return cast;

            bool CanCastDefensiveSpell(Spell spell, out GameLiving target)
            {
                target = null;
                if (CompanionFollowPolicy.DeferBuff(BotBody, spell)) return false;

                if (spell.Level > Body.Level || Body.Mana < BotBody.PowerCost(spell) ||
                    !BotBody.CanAffordConcentration(spell) ||
                    BotSongTwistPolicy.IsReservedPulse(BotBody, spell) ||
                    spell.NeedInstrument || (!spell.Uninterruptible && Body.IsBeingInterrupted) ||
                    (spell.HasRecastDelay && Body.GetSkillDisabledDuration(spell) > 0))
                {
                    return false;
                }

                target = FindTargetForDefensiveSpell(spell);
                return target != null;
            }
        }

        protected virtual GameLiving FindTargetForDefensiveSpell(Spell spell)
        {
            // Do not let the secondary defensive selector undo stationary PBT
            // upkeep by starting caster speed again on the next FOLLOW pulse.
            if (spell.SpellType == eSpellType.SpeedEnhancement && spell.IsPulsing && !IsMaintenanceTraveling())
                return null;
            if (spell.Target == eSpellTarget.REALM && !spell.IsPulsing && IsMaintainableClassBuff(spell))
                return FindRandomMissingRealmBuffTarget(spell);
            GameLiving target = null;

            switch (spell.SpellType)
            {
                case eSpellType.SpeedEnhancement when spell.IsPulsing:
                    if (!LivingHasEffect(Body, spell))
                        target = Body;
                    break;

                case eSpellType.BodySpiritEnergyBuff:
                case eSpellType.HeatColdMatterBuff:
                case eSpellType.AblativeArmor:
                case eSpellType.AcuityBuff:
                case eSpellType.AFHitsBuff:
                case eSpellType.ArmorAbsorptionBuff:
                case eSpellType.BaseArmorFactorBuff:
                case eSpellType.SpecArmorFactorBuff:
                case eSpellType.PaladinArmorFactorBuff:
                case eSpellType.Buff:
                case eSpellType.ConstitutionBuff:
                case eSpellType.CourageBuff:
                case eSpellType.CrushSlashTrustBuff:
                case eSpellType.DexterityBuff:
                case eSpellType.DexterityQuicknessBuff:
                case eSpellType.HealthRegenBuff:
                case eSpellType.StrengthBuff:
                case eSpellType.StrengthConstitutionBuff:
                case eSpellType.SuperiorCourageBuff:
                case eSpellType.DamageAdd:
                case eSpellType.OffensiveProc:
                case eSpellType.DefensiveProc:
                case eSpellType.Bladeturn when !spell.IsPulsing:
                    if (!LivingHasEffect(Body, spell))
                    {
                        target = Body;
                        break;
                    }

                    if (Body.Group != null && (spell.Target == eSpellTarget.REALM || spell.Target == eSpellTarget.GROUP))
                    {
                        foreach (GameLiving groupMember in Body.Group.GetMembersInTheGroup())
                        {
                            if (groupMember != Body && !LivingHasEffect(groupMember, spell) && Body.IsWithinRadius(groupMember, spell.Range) && groupMember.IsAlive)
                            {
                                target = groupMember;
                                break;
                            }
                        }
                    }
                    if (target == null)
                        target = FindMissingPartyPetBuffTarget(spell);
                    break;
            }

            return target;
        }

        // Mirrors SpellHandler.HasConflictingEffectWith.
        public static bool DotsConflict(Spell spell, Spell existing) =>
            spell.ID == existing.ID ||
            (spell.EffectGroup != 0 || existing.EffectGroup != 0
                ? spell.EffectGroup == existing.EffectGroup
                : spell.SpellType == existing.SpellType);

        public bool LivingHasEffect(GameLiving target, Spell spell)
        {
            if (target == null)
                return true;

            eEffect spellEffect = EffectHelper.GetEffectFromSpell(spell);

            if (spellEffect is eEffect.DirectDamage or eEffect.Pet or eEffect.Unknown)
                return false;

            ISpellHandler spellHandler = Body.castingComponent.SpellHandler;

            if (spellHandler != null && spellHandler.Spell.ID == spell.ID && spellHandler.Target == target)
                return true;

            ISpellHandler queuedSpellHandler = Body.castingComponent.QueuedSpellHandler;

            if (queuedSpellHandler != null && queuedSpellHandler.Spell.ID == spell.ID && queuedSpellHandler.Target == target)
                return true;

            if (spell.SpellType is eSpellType.OffensiveProc or eSpellType.DefensiveProc)
            {
                List<ECSGameSpellEffect> existingEffects = target.effectListComponent.GetSpellEffects(spellEffect);

                foreach (ECSGameSpellEffect effect in existingEffects)
                {
                    if (effect.SpellHandler.Spell.ID == spell.ID || (spell.EffectGroup > 0 && effect.SpellHandler.Spell.EffectGroup == spell.EffectGroup))
                        return true;
                    // A same-or-higher rank of this proc line already wins
                    // the effect conflict; recasting a lower rank only burns power.
                    if (spell.Group != 0 && effect.SpellHandler.Spell.Group == spell.Group &&
                        effect.SpellHandler.Spell.Level >= spell.Level)
                        return true;
                }

                return false;
            }

            // Every native Savage self-buff intentionally shares eEffect.SavageBuff.
            // A category-only lookup therefore made the first active buff suppress
            // all other DPS, haste, parry, evade, and resistance families.
            if (spellEffect == eEffect.SavageBuff)
                return target.effectListComponent.GetEffects(eEffect.SavageBuff)
                    .Any(effect => SavageBotCombatPolicy.SameBuffFamily(
                        spell, effect?.SpellHandler?.Spell));

            ECSGameEffect pulseEffect = EffectListService.GetPulseEffectOnTarget(target, spell);

            if (pulseEffect != null)
                return true;

            // DoTs from different effect groups stack (Sluaghbinder Rot + Bane), so
            // only a DoT the server would treat as conflicting blocks a new one.
            if (spellEffect == eEffect.DamageOverTime)
                return target.effectListComponent.GetSpellEffects(spellEffect)
                    .Any(effect => effect?.SpellHandler?.Spell is Spell existing && DotsConflict(spell, existing));

            return EffectListService.GetEffectOnTarget(target, spellEffect) != null || HasImmunityEffect(EffectHelper.GetImmunityEffectFromSpell(spell)) || HasImmunityEffect(EffectHelper.GetNpcImmunityEffectFromSpell(spell));

            bool HasImmunityEffect(eEffect immunityEffect)
            {
                return immunityEffect is not eEffect.Unknown && EffectListService.GetEffectOnTarget(target, immunityEffect) != null;
            }
        }

        #endregion

        #region Healing

        private long nextCureTime = 0;

        bool CheckHealSpell(Spell spell)
        {
            return spell != null
                && (!BotBody.IsBeingInterruptedByOther || spell.IsInstantCast)
                && (!spell.HasRecastDelay || BotBody.GetSkillDisabledDuration(spell) <= 0)
                && BotBody.Mana >= BotBody.PowerCost(spell);
        }

        private readonly BotSupportReach _supportReach = new();

        /// <summary>
        /// A group member a heal or buff may be aimed at: this bot, or a member within
        /// 2,500 units in the same region that is not being skipped after a failed approach.
        /// </summary>
        private bool InSupportReach(GameLiving member) =>
            member == Body || member != null && member.CurrentRegion == Body.CurrentRegion &&
            Body.IsWithinRadius(member, BotSupportReach.SupportRadius) &&
            !_supportReach.IsSkipped(member, GameLoop.GameLoopTime);

        /// <summary>
        /// Walks toward a group member that is out of range of a heal or buff. False once the
        /// walk has made no progress for 12 seconds (that member is then skipped for 30).
        /// </summary>
        private bool ApproachSupportTarget(GameLiving target)
        {
            if (target == null || target == Body || !InSupportReach(target) ||
                !_supportReach.KeepApproaching(target, Body.GetDistanceTo(target), GameLoop.GameLoopTime))
                return false;
            BotBody.WalkTo(new Point3D(target.X, target.Y, target.Z), BotBody.MaxSpeed);
            return true;
        }

        public bool CheckHeals()
        {
            const byte ManaThreshold = 90;
            const long CureDelay = 5000;

            if (AlreadyCheckedHeals || (!Body.CanCastHealSpells && !(Body.Group != null && BotBody.ResurrectionSpell != null)) || Body.IsStunned || Body.IsMezzed || Body.IsSilenced)
                return false;

            AlreadyCheckedHeals = true;

            if (TryResurrectGroupMember())
                return true;

            if (!Body.CanCastHealSpells) return false;

            // Check if we can cast any instant heals
            bool canCastInstantHeal = CheckHealSpell(BotBody.HealInstant);
            bool canCastInstantGroupHeal = CheckHealSpell(BotBody.HealInstantGroup);

            if (BotBody.IsBeingInterruptedByOther && !canCastInstantHeal && !canCastInstantGroupHeal)
                return false;

            bool isCastingHeal = BotBody.IsCasting && BotBody.castingComponent.SpellHandler.Spell.IsHealing &&
                !BotSongTwistPolicy.HasMobileSongCast(BotBody);

            if (isCastingHeal && !canCastInstantHeal && !canCastInstantGroupHeal)
                return true;

            // Working variables
            int amountToHeal = 0;
            int numEmergency = 0;
            int numNeedHealing = 0;
            Spell spellToCast = null;
            GameLiving spellTarget = null;
            bool startedCasting = false;

            // Check group health
            if (Body.Group != null)
            {
                foreach (GameLiving member in Body.Group.GetMembersInTheGroup())
                {
                    if (!member.IsAlive || !InSupportReach(member))
                        continue;

                    int deficit = member.MaxHealth - member.Health;

                    if (deficit > 0)
                    {
                        amountToHeal += deficit;

                        if (member.HealthPercent < 65)
                        {
                            numNeedHealing++;

                            if (member.HealthPercent < 40)
                                numEmergency++;

                            if (!BotBody.IsTemporaryGroupHelper
                                    ? spellTarget == null || member.HealthPercent < spellTarget.HealthPercent
                                    : TemporaryCompanionPetHealing.PreferHealingTarget(BotBody, member, spellTarget))
                                spellTarget = member;
                        }
                        else if (IsHealer && member.HealthPercent < 80)
                        {
                            numNeedHealing++;

                            if (!BotBody.IsTemporaryGroupHelper
                                    ? spellTarget == null || member.HealthPercent < spellTarget.HealthPercent
                                    : TemporaryCompanionPetHealing.PreferHealingTarget(BotBody, member, spellTarget))
                                spellTarget = member;
                        }
                    }
                }

                // A temporary companion treats the exact player-led party's
                // pets as party-adjacent heal targets. Attached pets are walked as a
                // bounded tree; the local-radius pass also finds Theurgist and
                // Animist field pets which native code intentionally leaves
                // outside ControlledBrain. Ownership must resolve to the player
                // or an exact-group temporary companion.
                if (BotBody.IsTemporaryGroupHelper)
                {
                    int fieldPetRange = new[]
                    {
                        BotBody.HealInstant, BotBody.HealBig, BotBody.HealEfficient,
                        BotBody.HealOverTime, BotBody.HealGroup, BotBody.HealInstantGroup
                    }.Where(spell => spell != null)
                     .Select(spell => BotBody.castingComponent.CalculateSpellRange(spell))
                     .DefaultIfEmpty(0).Max();

                    foreach (GameNPC playerPet in TemporaryCompanionPetHealing.TriageTargets(BotBody, fieldPetRange))
                    {
                        int deficit = playerPet.MaxHealth - playerPet.Health;
                        if (deficit <= 0)
                            continue;

                        amountToHeal += deficit;
                        if (playerPet.HealthPercent < 65)
                        {
                            numNeedHealing++;
                            if (playerPet.HealthPercent < 40)
                                numEmergency++;
                            if (TemporaryCompanionPetHealing.PreferHealingTarget(BotBody, playerPet, spellTarget))
                                spellTarget = playerPet;
                        }
                        else if (IsHealer && playerPet.HealthPercent < 80)
                        {
                            numNeedHealing++;
                            if (TemporaryCompanionPetHealing.PreferHealingTarget(BotBody, playerPet, spellTarget))
                                spellTarget = playerPet;
                        }
                    }
                }

                // A player-led bot party exists to support its player leader.  When the
                // leader needs a heal, prefer that player over autonomous bot members;
                // healthy leaders do not prevent the healer from tending the rest.
                GamePlayer assistedPlayer = AssistedPlayer;
                if (BotBody.IsPlayerLedGroup && assistedPlayer?.IsAlive == true && Body.Group.IsInTheGroup(assistedPlayer) &&
                    InSupportReach(assistedPlayer))
                {
                    bool playerNeedsHealing = assistedPlayer.HealthPercent < 65 || (IsHealer && assistedPlayer.HealthPercent < 80);
                    if (playerNeedsHealing)
                        spellTarget = assistedPlayer;
                }

                // A real relic carrier remains fully governed by native relic
                // rules.  For an allied group member in a legal heal range,
                // however, keeping that carrier alive takes precedence over
                // ordinary group triage.
                GameLiving relicCarrier = RelicMgr.GetRelics()
                    .Select(relic => relic?.CurrentCarrier)
                    .FirstOrDefault(carrier => carrier?.IsAlive == true && carrier.Realm == BotBody.Realm &&
                                               (Body.Group.IsInTheGroup(carrier) || BotBody.IsAutonomousWorldBot &&
                                                AutonomousObjectiveAssignments.Is(BotBody, eAutonomousObjectiveKind.RvR)) && CanHealRelicCarrier(carrier));
                if (relicCarrier != null && relicCarrier.HealthPercent < 100 &&
                    (!BotBody.IsTemporaryGroupHelper ||
                     TemporaryCompanionPetHealing.PreferHealingTarget(BotBody, relicCarrier, spellTarget)))
                {
                    spellTarget = relicCarrier;
                    // A 80-99% carrier may not have met ordinary triage's
                    // threshold, so make the existing non-emergency spell
                    // selection operational without inventing extra deficit or
                    // altering its real emergency threshold.
                    numNeedHealing = Math.Max(1, numNeedHealing);
                }
            }
            else
            {
                amountToHeal = BotBody.MaxHealth - BotBody.Health;

                if (amountToHeal > 0)
                {
                    spellTarget = BotBody;

                    if (BotBody.HealthPercent < 65)
                    {
                        numNeedHealing = 1;

                        if (BotBody.HealthPercent < 40)
                            numEmergency = 1;
                    }
                }
            }

            // RvR support uses ordinary, range/LOS-checked single-target heals.
            // This does not extend PvE/companion triage to unrelated NPCs.
            if (BotBody.IsAutonomousWorldBot && AutonomousObjectiveAssignments.Is(BotBody, eAutonomousObjectiveKind.RvR))
            {
                GameLiving support = RelicMgr.GetRelics().Select(relic => relic.CurrentCarrier)
                    .Concat(Body.GetNPCsInRadius(2000).OfType<DOL.GS.Keeps.GameKeepGuard>().Cast<GameLiving>())
                    .Where(living => living?.IsAlive == true && living.Realm == Body.Realm && living.HealthPercent < 85 &&
                        living.CurrentRegion == Body.CurrentRegion && CanHealRelicCarrier(living) &&
                        PathfindingProvider.Instance.HasLineOfSight(Body.CurrentZone, new(Body.X, Body.Y, Body.Z),
                            new(living.X, living.Y, living.Z), PathfindingProvider.Instance.DefaultFilters))
                    .OrderBy(living => GameRelic.IsPlayerCarryingRelic(living) ? 0 : living is DOL.GS.Keeps.GuardLord ? 1 : 2)
                    .ThenBy(living => living.HealthPercent).FirstOrDefault();
                if (support != null && (spellTarget == null || spellTarget.HealthPercent >= 40 || support.HealthPercent < spellTarget.HealthPercent))
                {
                    spellTarget = support;
                    amountToHeal = Math.Max(amountToHeal, support.MaxHealth - support.Health);
                    numNeedHealing = Math.Max(1, numNeedHealing);
                    if (support.HealthPercent < 40) numEmergency = Math.Max(1, numEmergency);
                }
            }

            if (AutonomousRealmRaid.HasSharedSupport(BotBody))
            {
                // Lowest-health raid member or pet in range, without sorting the whole roster.
                GameLiving raidPatient = null;
                foreach (GameLiving m in AutonomousRealmRaid.SupportMembers(BotBody))
                    if (m != null && m.IsAlive && m.CurrentRegionID == Body.CurrentRegionID && m.HealthPercent < 80 &&
                        Body.IsWithinRadius(m, 1800) && (raidPatient == null || m.HealthPercent < raidPatient.HealthPercent))
                        raidPatient = m;
                foreach (GameLiving m in AutonomousRealmRaid.SupportPets(BotBody, 1800))
                    if (m.HealthPercent < 80 && (raidPatient == null || m.HealthPercent < raidPatient.HealthPercent))
                        raidPatient = m;
                if (raidPatient != null && (spellTarget == null || raidPatient.HealthPercent < spellTarget.HealthPercent))
                {
                    spellTarget = raidPatient;
                    numNeedHealing = Math.Max(1, numNeedHealing);
                    if (raidPatient.HealthPercent < 40) numEmergency = Math.Max(1, numEmergency);
                }
            }

            // Emergency heal
            if (numEmergency > 0)
            {
                if (canCastInstantHeal)
                    spellToCast = BotBody.HealInstant;
                else if (canCastInstantGroupHeal)
                    spellToCast = BotBody.HealInstantGroup;
                else if (!isCastingHeal)
                {
                    if (CheckHealSpell(BotBody.HealBig))
                        spellToCast = BotBody.HealBig;
                    else if (CheckHealSpell(BotBody.HealEfficient))
                        spellToCast = BotBody.HealEfficient;
                    else if (CheckHealSpell(BotBody.HealGroup))
                        spellToCast = BotBody.HealGroup;
                }
            }

            // Cure Mezz/Disease/Poison
            if (spellToCast == null)
            {
                if (Body.Group != null)
                {
                    foreach (GameLiving member in Body.Group.GetMembersInTheGroup().OrderBy(member => member == AssistedPlayer ? 0 : 1))
                    {
                        if (member.IsMezzed && member != Body && InSupportReach(member) && CheckHealSpell(BotBody.CureMezz))
                        {
                            spellToCast = BotBody.CureMezz;
                            spellTarget = member;
                            break;
                        }
                    }

                    if (spellToCast == null && nextCureTime < GameLoop.GameLoopTime)
                    {
                        foreach (GameLiving member in Body.Group.GetMembersInTheGroup().OrderBy(member => member == AssistedPlayer ? 0 : 1))
                        {
                            if (!InSupportReach(member))
                                continue;
                            if (member.IsDiseased)
                            {
                                if (CheckHealSpell(BotBody.CureDisease))
                                {
                                    spellToCast = BotBody.CureDisease;
                                    spellTarget = member;
                                    break;
                                }
                                else if (CheckHealSpell(BotBody.CureDiseaseGroup))
                                {
                                    spellToCast = BotBody.CureDiseaseGroup;
                                    spellTarget = member;
                                    break;
                                }
                            }

                            if (member.IsPoisoned)
                            {
                                if (CheckHealSpell(BotBody.CurePoison))
                                {
                                    spellToCast = BotBody.CurePoison;
                                    spellTarget = member;
                                    break;
                                }
                                else if (CheckHealSpell(BotBody.CurePoisonGroup))
                                {
                                    spellToCast = BotBody.CurePoisonGroup;
                                    spellTarget = member;
                                    break;
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (BotBody.IsDiseased && nextCureTime < GameLoop.GameLoopTime && CheckHealSpell(BotBody.CureDisease))
                    {
                        spellToCast = BotBody.CureDisease;
                        spellTarget = BotBody;
                    }
                    else if (BotBody.IsPoisoned && nextCureTime < GameLoop.GameLoopTime && CheckHealSpell(BotBody.CurePoison))
                    {
                        spellToCast = BotBody.CurePoison;
                        spellTarget = BotBody;
                    }
                }
            }

            // Non-emergency heal
            if (spellToCast == null && numNeedHealing > 0)
            {
                if (!BotBody.IsCasting || (numEmergency > 0 && !isCastingHeal))
                {
                    if (CheckHealSpell(BotBody.HealOverTime) && !LivingHasEffect(spellTarget, BotBody.HealOverTime))
                        spellToCast = BotBody.HealOverTime;
                    else if (BotBody.ManaPercent >= ManaThreshold && CheckHealSpell(BotBody.HealBig)
                        && (spellTarget.MaxHealth - spellTarget.Health) >= GameBot.HealAmount(BotBody.HealBig, spellTarget))
                        spellToCast = BotBody.HealBig;
                    else if (CheckHealSpell(BotBody.HealEfficient))
                        spellToCast = BotBody.HealEfficient;
                    else if (CheckHealSpell(BotBody.HealGroup))
                        spellToCast = BotBody.HealGroup;
                }
            }

            // Cast the selected spell
            if (BotBody.IsAutonomousWorldBot && (AutonomousObjectiveAssignments.Is(BotBody, eAutonomousObjectiveKind.RvR) || AutonomousRealmRaid.GetView(BotBody.Group) != null) &&
                spellTarget != null && spellTarget != Body && Body.Group?.IsInTheGroup(spellTarget) != true &&
                (spellToCast == BotBody.HealGroup || spellToCast == BotBody.HealInstantGroup))
                spellToCast = CheckHealSpell(BotBody.HealBig) ? BotBody.HealBig :
                    CheckHealSpell(BotBody.HealEfficient) ? BotBody.HealEfficient :
                    CheckHealSpell(BotBody.HealInstant) ? BotBody.HealInstant : null;
            if (spellToCast != null && spellTarget != null)
            {
                spellTarget = BotGroupSupport.ReserveHeal(BotBody, spellToCast, spellTarget);
                if (spellTarget == null) return isCastingHeal;
                if (!BotBody.IsWithinRadius(spellTarget, BotBody.castingComponent.CalculateSpellRange(spellToCast)))
                {
                    // Walk into range; give up on a member it cannot get closer to.
                    return ApproachSupportTarget(spellTarget) || isCastingHeal;
                }
                _supportReach.Reached(spellTarget);

                if (!spellToCast.IsInstantCast)
                {
                    if (BotBody.IsCasting)
                        BotBody.StopCurrentSpellcast();
                    else if (BotBody.IsAttacking)
                        BotBody.StopAttack();
                }

                GameObject oldTarget = BotBody.TargetObject;
                BotBody.TargetObject = spellTarget;
                startedCasting = BotBody.CastSpell(spellToCast, m_mobSpellLine, false);

                if (!startedCasting)
                {
                    BotGroupSupport.CancelHeal(BotBody);
                    BotBody.TargetObject = oldTarget;
                }
                else
                {
                    if (spellToCast.IsInstantCast)
                    {
                        BotBody.TargetObject = oldTarget;
                        startedCasting = false;
                    }
                    else if (spellToCast.SpellType == eSpellType.CureDisease || spellToCast.SpellType == eSpellType.CurePoison)
                        nextCureTime = GameLoop.GameLoopTime + CureDelay;
                }
            }

            return startedCasting || isCastingHeal;
        }

        private bool CanHealRelicCarrier(GameLiving carrier)
        {
            Spell[] heals = [BotBody.HealInstant, BotBody.HealBig, BotBody.HealEfficient, BotBody.HealOverTime];
            return heals.Any(spell => spell != null && BotBody.IsWithinRadius(carrier, BotBody.castingComponent.CalculateSpellRange(spell)));
        }

        private long _nextCompanionResurrectionTick;

        private bool TryResurrectCompanionOwner()
        {
            GameBot bot = BotBody;
            if (bot?.IsTemporaryGroupHelper != true) return false;
            GamePlayer owner = bot.Owner;
            Spell resurrection = bot.ResurrectionSpell;
            if (owner == null || owner.IsAlive || resurrection == null || owner.ObjectState != GameObject.eObjectState.Active ||
                owner.CurrentRegionID != bot.CurrentRegionID ||
                !TemporaryCompanionRecovery.CanPrioritizeOwnerResurrection(true, bot.IsAlive, owner.IsAlive,
                    bot.Group != null && bot.Group == owner.Group && bot.Group.IsInTheGroup(bot) && bot.Group.IsInTheGroup(owner),
                    bot.PlayerGroupLeader == owner, resurrection != null && resurrection.Level <= bot.Level,
                    TemporaryCompanionRecovery.HasPartyCombat(bot)))
                return false;

            if (bot.IsCasting) return true;
            if (bot.IsCrowdControlled || bot.IsSilenced) return false;
            if (GameLoop.GameLoopTime < _nextCompanionResurrectionTick) return true;
            _nextCompanionResurrectionTick = GameLoop.GameLoopTime + 2000;
            // Use the native resurrection cost for this corpse, not the generic
            // spell.Power field (resurrection overrides its power calculation).
            var handler = ScriptMgr.CreateSpellHandler(bot, resurrection, m_mobSpellLine);
            if (handler == null) return false;
            int requiredPower = Math.Max(bot.PowerCost(resurrection), handler.PowerCost(owner));
            if (bot.Mana < requiredPower)
            {
                bot.BeginTemporaryCompanionRest();
                return true;
            }
            if (owner.TempProperties.GetProperty<GameLiving>("RESURRECT_CASTER") != null)
                return true; // The normal accept/decline prompt is already open.
            bot.WakeRecoveryRest();
            if (!CheckHealSpell(resurrection)) return true;
            return TryResurrectGroupMember();
        }

        private long _nextResurrectionBlockReportTick;

        // Resurrection outranks chant and song upkeep. A Paladin's chant (or a
        // bard's song) was queued early in the think; the resurrection later in
        // the same think was then refused because a cast was already pending
        // (31 Paladin "cast refused" reports in one evening run, Albion holding
        // 570 of 950 resurrection timeouts). Upkeep resumes once nobody waits.
        private bool HasResurrectionDuty()
        {
            GameBot bot = BotBody;
            Spell resurrection = bot?.ResurrectionSpell;
            if (resurrection == null || bot.Group == null || !bot.IsAlive)
                return false;
            int range = bot.castingComponent.CalculateSpellRange(resurrection);
            bool corpseWaiting = bot.Group.GetMembersInTheGroup().Any(member => member != bot && !member.IsAlive &&
                member.ObjectState == GameObject.eObjectState.Active && member.CurrentRegionID == bot.CurrentRegionID &&
                bot.IsWithinRadius(member, range) && member.TempProperties.GetProperty<GameLiving>("RESURRECT_CASTER") == null);
            return BotGroupSupport.ResurrectionOutranksUpkeep(true, true, bot.Mana >= bot.PowerCost(resurrection), corpseWaiting);
        }

        // Diagnostic for failed group resurrections (an in-range resurrector stood idle
        // for the whole corpse wait in hundreds of cases). At most one line per bot per
        // minute, and only while a dead group member is actually within spell range.
        private void ReportResurrectionBlock(Spell resurrection, string reason, GameLiving corpse = null)
        {
            if (Body.Group == null || !BotBody.IsAutonomousWorldBot || GameLoop.GameLoopTime < _nextResurrectionBlockReportTick)
                return;
            int range = BotBody.castingComponent.CalculateSpellRange(resurrection);
            corpse ??= Body.Group.GetMembersInTheGroup().FirstOrDefault(member => member != Body && !member.IsAlive &&
                member.ObjectState == GameObject.eObjectState.Active && member.CurrentRegionID == Body.CurrentRegionID &&
                Body.IsWithinRadius(member, range));
            if (corpse == null)
                return;
            _nextResurrectionBlockReportTick = GameLoop.GameLoopTime + 60_000;
            reason ??= BotGroupSupport.DescribeResurrectionBlock(BotBody, resurrection, corpse);
            log.Info($"AUTONOMOUS_RESURRECTION_BLOCKED bot=\"{BotBody.Name}\" class=\"{BotBody.ClassName}\" corpse=\"{corpse.Name}\" " +
                     $"distance={Body.GetDistanceTo(corpse)} spell=\"{resurrection.Name}\" mana={BotBody.Mana}/{BotBody.MaxMana} " +
                     $"inCombat={Body.InCombat} reason=\"{reason}\"");
        }

        private bool TryResurrectGroupMember()
        {
            Spell resurrection = BotBody.ResurrectionSpell;
            if (Body.Group != null)
            {
                if (Body.IsCasting && Body.castingComponent.SpellHandler?.Spell?.SpellType == eSpellType.Resurrect)
                    return BotGroupSupport.ContinueResurrection(BotBody);
                if (resurrection == null) return false;
                if (Body.IsCasting || !CheckHealSpell(resurrection))
                {
                    ReportResurrectionBlock(resurrection, Body.IsCasting ? "casting another spell" :
                        BotBody.Mana < BotBody.PowerCost(resurrection) ? $"not enough power to cast ({BotBody.Mana}/{BotBody.PowerCost(resurrection)})" :
                        BotBody.IsBeingInterruptedByOther ? "being interrupted" : "resurrection on recast");
                    return false;
                }
                GameLiving corpse = BotGroupSupport.ReserveResurrection(BotBody, resurrection);
                if (corpse != null)
                {
                    GameObject previous = BotBody.TargetObject;
                    BotBody.attackComponent.StopAttack();
                    BotBody.StopFollowing();
                    BotBody.StopMoving();
                    BotBody.TargetObject = corpse;
                    if (BotBody.CastSpell(resurrection, m_mobSpellLine, false)) return true;
                    BotBody.TargetObject = previous;
                    BotGroupSupport.CancelResurrection(BotBody);
                    ReportResurrectionBlock(resurrection, BotBody.castingComponent.HasPendingSkillRequests
                        ? "another cast was already queued" : "the resurrection cast was refused", corpse);
                    return false;
                }
                ReportResurrectionBlock(resurrection, null);
            }
            if (resurrection == null || Body.Group == null || Body.InCombat || Body.Group.GetMembersInTheGroup().Any(m => m.IsAlive && m.InCombat) || Body.IsCasting || !CheckHealSpell(resurrection))
                return false;

            int range = BotBody.castingComponent.CalculateSpellRange(resurrection);
            bool regrouping = AutonomousBotGroupCoordinator.IsRecovering(BotBody);
            GameLiving deadMember = Body.Group.GetMembersInTheGroup()
                .Where(member => member != Body && !member.IsAlive && member.ObjectState is GameObject.eObjectState.Active &&
                                 member.CurrentRegionID == Body.CurrentRegionID)
                // Raise nearby casualties, but do not abandon recovery to chase
                // a distant corpse back into the failed pull/dungeon room.
                .Where(member => !regrouping || BotBody.IsWithinRadius(member, range))
                .OrderBy(member => member == AssistedPlayer ? 0 : 1)
                .ThenBy(Body.GetDistanceTo)
                .FirstOrDefault();
            if (deadMember == null)
                return false;

            if (!BotBody.IsWithinRadius(deadMember, range) || !BotGroupSupport.HasCorpseLineOfSight(BotBody, deadMember))
            {
                Vector3 current = new(BotBody.X, BotBody.Y, BotBody.Z);
                Vector3 desired = new(deadMember.X, deadMember.Y, deadMember.Z);
                AutonomousThreatAwarePathing.SafeStep step = AutonomousThreatAwarePathing.ChooseStep(BotBody, current, desired, 900);
                BotBody.PathTo(step.Position, BotBody.MaxSpeed);
                return true;
            }

            // In-range corpses must obtain the shared reservation above.
            return false;
        }

        #endregion
    }
}
