using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using DOL.Logging;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.Effects;
using DOL.GS.GameEvents;
using DOL.GS.Movement;
using DOL.GS.PacketHandler;
using DOL.GS.PropertyCalc;
using DOL.GS.Realm;
using DOL.GS.RealmAbilities;
using DOL.GS.Styles;
using static DOL.GS.GamePlayer;

namespace DOL.GS
{
    public enum eBotStance : byte
    {
        Auto = 0,
        Melee = 1,
        Ranged = 2,
        Passive = 3
    }

    public class GameBot : GameNPC, IGamePlayer, IGameStaticItemOwner
    {
        private static new readonly Logger log = LoggerManager.Create(typeof(GameBot));

        #region Core Properties

        public GamePlayer Owner { get; private set; }
        public GamePlayer PlayerGroupLeader { get; private set; }
        public bool IsPlayerLedGroup => PlayerGroupLeader != null;
        public string ClassName { get; private set; }
        public new string InternalID { get; set; }
        public byte ClassId { get; set; }
        public byte RaceId { get; set; }
        public byte GenderId { get; set; }
        public long DatabaseID { get; set; }
        public bool IsAutonomousWorldBot { get; private set; }
        public bool IsTemporaryGroupHelper { get; private set; }
        public bool IsEndgameCompanion => TemporaryCompanionBalance.IsEndgame(IsTemporaryGroupHelper, Level);
        private bool _endgameCompanionEquipped;
        public bool SuppressLootAndProgress => IsTemporaryGroupHelper;
        internal int EquipmentLevelFloor { get; private set; }
        internal int EquipmentLevelCap { get; private set; }
        public OfflineWorldBotRecord PersistentRecord { get; private set; }
        internal AutonomousGoalAttempt GoalDiagnosticAttempt;
        private byte _lastAutonomousTrainedLevel = 1;
        public bool HasPendingAutonomousTraining => IsAutonomousWorldBot && _lastAutonomousTrainedLevel < Level;
        /// <summary>
        /// Spec points to spend, or enough realm ability points to be worth the trip (both at the class
        /// trainer; a few realm points are spent at the next level-up visit instead).
        /// </summary>
        public bool HasSpendableAutonomousTrainingPoints =>
            HasSpendableAutonomousSpecPoints || AutonomousBotRealmAbilities.WorthTrainerTrip(this);
        private bool HasSpendableAutonomousSpecPoints
        {
            get
            {
                if (!HasPendingAutonomousTraining || BotSpec == null || IsTemporaryGroupHelper)
                    return false;
                int points = m_leftOverSpecPoints;
                bool catchingUp = Level - _lastAutonomousTrainedLevel > 1;
                for (int level = _lastAutonomousTrainedLevel + 1; level <= Level; level++)
                    points += GetSpecPointsForLevel(level, catchingUp);
                return BotSpec.SpecLines.Any(line => (line.levelRatio > 0 || Level >= 50) &&
                    GetSpecializationByName(line.Spec) is Specialization spec && spec.Trainable &&
                    spec.Level < Math.Min(Level, line.SpecCap) && points >= spec.Level + 1);
            }
        }
        public long Experience { get; private set; }
        public long AutonomousRealmPoints { get; private set; }
        public bool AutonomousStateDirty { get; private set; }
        public eAutonomousFidelity AutonomousFidelity { get; set; } = eAutonomousFidelity.Efficient;
        public bool IsOnStableMasterRoute { get; private set; }
        public string StableRouteDestination { get; private set; } = string.Empty;
        private DateTime _stableRouteExpectedArrivalUtc;
        private GameTaxi _stableRouteMount;
        private ECSGameTimer _stableRouteDepartureTimer;
        private bool _stableRouteDeparturePending;
        private PathPoint _stableRouteStart;
        private Point3D _stableRouteEndpoint;
        private int _stableRouteRecoveryCount;
        private readonly ConcurrentDictionary<GamePlayer, StableRideObserver> _stableRideViewers = new();
        private long _nextStableViewerRefreshTick;
        private long _stableRouteLastMovementTick;
        private Point3D _stableRouteLastPosition;
        private ECSGameTimer _deathRecoveryTimer;
        private long _deathTick;
        private ushort _deathRegionId;
        private Point3D _deathLocation;
        private long _nextReturnRoleplayTick;
        private AutonomousStableRoutePlanner.Choice _stableReturnPlan;
        // GameObject.MoveTo removes and re-adds an actor. GameNPC.RemoveFromWorld
        // stops its brain, and brain shutdown can itself request a MoveTo. Keep a
        // depth rather than a boolean so a nested request cannot clear the outer
        // transfer scope and turn a legitimate zone change into a logout/save.
        private int _intentionalWorldMoveDepth;
        internal bool IsIntentionalWorldMove => Volatile.Read(ref _intentionalWorldMoveDepth) > 0;

        private void BeginIntentionalWorldMove() => Interlocked.Increment(ref _intentionalWorldMoveDepth);

        private bool EndIntentionalWorldMove() => Interlocked.Decrement(ref _intentionalWorldMoveDepth) == 0;
        public bool IsReturningAfterRelease { get; private set; }
        public BotSpec BotSpec { get; private set; }
        public eBotStance Stance { get; set; } = eBotStance.Auto;
        private int m_leftOverSpecPoints;
        private long _nextWeaponRepairWarningTick;

        public override IList GetExamineMessages(GamePlayer player)
        {
            if (IsAutonomousWorldBot && player != null && Realm != eRealm.None && player.Realm != eRealm.None &&
                Realm != player.Realm)
            {
                return new ArrayList(1)
                {
                    $"You examine {GetName(0, false)}. {GetPronoun(0, true)} is a member of an enemy faction."
                };
            }
            return base.GetExamineMessages(player);
        }

        #endregion

        #region IGamePlayer — DummyClient / DummyPacketLib

        private readonly BotDummyPacketLib _dummyLib;
        private readonly BotDummyClient _dummyClient;

        public IPacketLib Out => _dummyLib;
        public GameClient Client => _dummyClient;

        #endregion

        #region IGamePlayer — CharacterClass

        protected ICharacterClass m_characterClass;

        public virtual ICharacterClass CharacterClass => m_characterClass;

        public virtual bool SetCharacterClass(int id)
        {
            ICharacterClass cl = ScriptMgr.FindCharacterClass(id);
            if (cl == null)
            {
                if (log.IsErrorEnabled)
                    log.ErrorFormat("No CharacterClass with ID {0} found", id);
                return false;
            }

            m_characterClass = cl;
            // Note: ICharacterClass.Init() expects GamePlayer. We skip it for GameBot
            // since GameBot is not a GamePlayer. The class identity (ID, stats, etc.)
            // is still usable without Init().

            if (Group != null)
                Group.UpdateMember(this, false, true);

            return true;
        }

        #endregion

        #region IGamePlayer — ECS Components (Explicit Interface)

        // The ECS components are public fields on GameLiving (lowercase).
        // IGamePlayer expects PascalCase properties.
        AttackComponent IGamePlayer.AttackComponent => attackComponent;
        RangeAttackComponent IGamePlayer.RangeAttackComponent => rangeAttackComponent;
        StyleComponent IGamePlayer.StyleComponent => styleComponent;
        EffectListComponent IGamePlayer.EffectListComponent => effectListComponent;

        #endregion

        #region IGamePlayer — Property Indexers (Explicit)

        // GameLiving returns PropertyIndexer (concrete) but IGamePlayer expects IPropertyIndexer.
        IPropertyIndexer IGamePlayer.AbilityBonus => AbilityBonus;
        IPropertyIndexer IGamePlayer.ItemBonus => ItemBonus;
        IPropertyIndexer IGamePlayer.BaseBuffBonusCategory => BaseBuffBonusCategory;
        IPropertyIndexer IGamePlayer.SpecBuffBonusCategory => SpecBuffBonusCategory;
        IPropertyIndexer IGamePlayer.DebuffCategory => DebuffCategory;

        public PropertyIndexer BuffBonusCategory4 { get; } = new();

        IPropertyIndexer IGamePlayer.BuffBonusCategory4 => BuffBonusCategory4;
        IPropertyIndexer IGamePlayer.OtherBonus => OtherBonus;
        IPropertyIndexer IGamePlayer.SpecDebuffCategory => SpecDebuffCategory;

        #endregion

        #region IGamePlayer — Stats (Explicit Interface — short vs int mismatch)

        int IGamePlayer.Strength => Strength;
        int IGamePlayer.Dexterity => Dexterity;
        int IGamePlayer.Quickness => Quickness;
        int IGamePlayer.Intelligence => Intelligence;

        #endregion

        #region IGamePlayer — Health/Mana/Endurance Overrides

        public override int Health
        {
            get => base.Health;
            set
            {
                byte oldPercent = HealthPercent;
                base.Health = value;

                if (oldPercent != HealthPercent)
                    Group?.UpdateMember(this, false, false);
            }
        }

        public override int Mana
        {
            get => m_mana;
            set
            {
                byte oldPercent = ManaPercent;
                int maxMana = MaxMana;
                m_mana = Math.Clamp(value, 0, maxMana);

                if (m_mana < maxMana)
                    StartPowerRegeneration();

                if (oldPercent != ManaPercent)
                    Group?.UpdateMember(this, false, false);
            }
        }

        public override int MaxMana => GetModified(eProperty.MaxMana);

        public override int MaxConcentration => GetModified(eProperty.MaxConcentration);
        public override int Concentration => MaxConcentration - (effectListComponent?.UsedConcentration ?? 0);

        // Bot buffs retain their real point budget, but not the human 20-effect cap.
        // Check before queuing a cast so exhausted buffers can move/rest instead of retrying.
        public bool CanAffordConcentration(Spell spell) => spell != null &&
            (spell.Concentration == 0 || spell.Concentration <= Concentration);

        public override int Endurance
        {
            get => m_endurance;
            set
            {
                byte oldPercent = EndurancePercent;
                int maxEndurance = MaxEndurance;
                m_endurance = Math.Clamp(value, 0, maxEndurance);

                if (m_endurance < maxEndurance)
                    StartEnduranceRegeneration();

                if (oldPercent != EndurancePercent)
                    Group?.UpdateMember(this, false, false);
            }
        }

        #endregion

        #region Player-accurate regeneration

        private int GetHealthAndPowerRegenerationInterval()
        {
            if (IsEnhancedResting) return BotRestRecovery.TickMilliseconds;
            // GameBots recover through the internal recovery lock; never expose
            // GameLiving.IsSitting to clients. Outside enhanced recovery they
            // use ordinary standing/combat timing.
            return ClassicRestRegeneration.HealthAndPowerInterval(false, InCombat);
        }

        protected override int GetHealthRegenerationInterval() => GetHealthAndPowerRegenerationInterval();

        protected override int GetPowerRegenerationInterval() => GetHealthAndPowerRegenerationInterval();

        protected override int GetEnduranceRegenerationInterval() => 1000;

        public bool IsRecoveryResting => _recoveryRestLocked;

        public bool IsEnhancedResting =>
            (IsRecoveryResting && !IsMoving && !BotRestRecovery.BlocksRest(this)) ||
            BotRestRecovery.ShouldAutonomousPlayerBotRecover(this, GameLoop.GameLoopTime);

        /// <summary>
        /// Moves an already-running ordinary regeneration timer onto the
        /// bot-only fast cadence when the two-second quiet window opens. The
        /// callback still rechecks combat, aggro, and casting, so this never
        /// grants recovery during an active encounter or changes normal NPC or
        /// real-player timers.
        /// </summary>
        internal void EnsureAutonomousRecoveryTimers()
        {
            if (!BotRestRecovery.ShouldAutonomousPlayerBotRecover(this, GameLoop.GameLoopTime) ||
                ObjectState is not eObjectState.Active)
                return;

            static void ArmFastTimer(ECSGameTimer timer)
            {
                if (timer == null || !timer.IsAlive || timer.Interval != BotRestRecovery.TickMilliseconds ||
                    timer.TimeUntilElapsed > BotRestRecovery.TickMilliseconds)
                    timer?.Start(BotRestRecovery.TickMilliseconds);
            }

            if (Health < MaxHealth)
                ArmFastTimer(m_healthRegenerationTimer);
            if (Mana < MaxMana)
                ArmFastTimer(m_powerRegenerationTimer);
            if (Endurance < MaxEndurance)
                ArmFastTimer(m_enduRegenerationTimer);
        }

        // GameNPC forwards controlled-pet combat timestamps to its owner. Wake
        // the bot on that same event instead of waiting for its next idle turn.
        // Do not change human combat flags, clear aggro or interrupt any casts.
        private void WakeFromRestOnCombat(long tick)
        {
            if (!IsRecoveryResting || !BotRestRecovery.RecentlyFought(GameLoop.GameLoopTime, tick)) return;
            WakeRecoveryRest();
            if (Brain is BotBrain brain) brain.NextThinkTick = GameLoop.GameLoopTime;
        }

        public override long LastAttackTickPvE
        {
            set { base.LastAttackTickPvE = value; WakeFromRestOnCombat(value); }
        }

        public override long LastAttackTickPvP
        {
            set { base.LastAttackTickPvP = value; WakeFromRestOnCombat(value); }
        }

        public override long LastAttackedByEnemyTickPvE
        {
            set { base.LastAttackedByEnemyTickPvE = value; WakeFromRestOnCombat(value); }
        }

        public override long LastAttackedByEnemyTickPvP
        {
            set { base.LastAttackedByEnemyTickPvP = value; WakeFromRestOnCombat(value); }
        }

        private void StartEnhancedRecoveryTimers()
        {
            if (!IsEnhancedResting || ObjectState is not eObjectState.Active) return;
            // Entering rest is a one-time transition. Replace a pending 14s
            // combat tick, but never restart timers on each ordinary AI turn.
            if (Health < MaxHealth) m_healthRegenerationTimer?.Start(1);
            if (Mana < MaxMana) m_powerRegenerationTimer?.Start(1);
            if (Endurance < MaxEndurance) m_enduRegenerationTimer?.Start(1);
        }

        private void WakeAfterRecovery()
        {
            if (IsRecoveryResting && AutonomousRestPolicy.IsFullyRecovered(HealthPercent, ManaPercent, EndurancePercent, MaxMana > 0) &&
                Brain is BotBrain brain && brain.NextThinkTick > GameLoop.GameLoopTime)
                brain.NextThinkTick = GameLoop.GameLoopTime;
        }

        protected override int HealthRegenerationTimerCallback(ECSGameTimer timer)
        {
            if (!IsEnhancedResting || Health >= MaxHealth) return base.HealthRegenerationTimerCallback(timer);
            int native = GetModified(eProperty.HealthRegenerationAmount);
            // Disease/bleed regen suppression remains authoritative.
            if (native > 0) ChangeHealth(this, eHealthChangeType.Regenerate, BotRestRecovery.RecoveryAmount(native, MaxHealth));
            WakeAfterRecovery();
            return BotRestRecovery.TickMilliseconds;
        }

        protected override int PowerRegenerationTimerCallback(ECSGameTimer timer)
        {
            if (!IsEnhancedResting || Mana >= MaxMana) return base.PowerRegenerationTimerCallback(timer);
            ChangeMana(this, eManaChangeType.Regenerate,
                BotRestRecovery.RecoveryAmount(GetModified(eProperty.PowerRegenerationAmount), MaxMana));
            WakeAfterRecovery();
            return BotRestRecovery.TickMilliseconds;
        }

        public override void StartPowerRegeneration()
        {
            if (m_health == 0 || ObjectState is not eObjectState.Active || m_powerRegenerationTimer.IsAlive || MaxMana <= 0)
                return;

            m_powerRegenerationTimer.Start(GetPowerRegenerationInterval());
        }

        public override void StartEnduranceRegeneration()
        {
            if (m_health == 0 || ObjectState is not eObjectState.Active || m_enduRegenerationTimer.IsAlive)
                return;

            m_enduRegenerationTimer.Start(GetEnduranceRegenerationInterval());
        }

        protected override int EnduranceRegenerationTimerCallback(ECSGameTimer selfRegenerationTimer)
        {
            int maxEndurance = MaxEndurance;
            // GameBots have lightweight free-running travel. Endurance is a
            // combat resource for styles/abilities and is never consumed merely
            // because a persistent bot or /spawn companion is moving quickly.
            if (Endurance >= maxEndurance)
            {
                Endurance = maxEndurance;
                return 0;
            }

            int regen = GetModified(eProperty.EnduranceRegenerationAmount);
            if (IsEnhancedResting) regen = BotRestRecovery.RecoveryAmount(regen, maxEndurance);
            if (regen != 0)
                ChangeEndurance(this, eEnduranceChangeType.Regenerate, regen);

            WakeAfterRecovery();

            return GetEnduranceRegenerationInterval();
        }

        #endregion

        #region IGamePlayer — Health/Mana Calculations

        public virtual int CalculateMaxHealth(int level, int constitution)
        {
            if (CharacterClass == null)
                return 1;

            // Keep this identical to GamePlayer.CalculateMaxHealth. Playerbots
            // must not receive the old NPC-oriented approximation, especially
            // at the first few levels where it granted a large flat HP surplus.
            constitution -= 50;
            if (constitution < 0)
                constitution *= 2;

            int hpFromLevel = CharacterClass.BaseHP * level;
            int hpFromConstitution = hpFromLevel * constitution / 10000;
            int hpFromChampionLevels = ChampionLevel >= 1
                ? ServerProperties.Properties.HPS_PER_CHAMPIONLEVEL * ChampionLevel
                : 0;
            double health = 20 + hpFromLevel / 50 + hpFromConstitution + hpFromChampionLevels;
            if (GetModified(eProperty.ExtraHP) > 0)
                health += Math.Round(health * GetModified(eProperty.ExtraHP) / 100d);

            return Math.Max(1, (int)health);
        }

        public virtual int CalculateMaxMana(int level, int manaStat)
        {
            if (CharacterClass == null || CharacterClass.ManaStat == eStat.UNDEFINED)
                return 0;

            return Math.Max(5, level * 5 + (manaStat - 50));
        }

        #endregion

        #region IGamePlayer — PlayerDeck / Misc

        private PlayerDeck _randomNumberDeck;

        public PlayerDeck RandomNumberDeck
        {
            get
            {
                if (_randomNumberDeck == null)
                    _randomNumberDeck = new PlayerDeck();
                return _randomNumberDeck;
            }
            set { _randomNumberDeck = value; }
        }

        public List<int> SelfBuffChargeIDs { get; } = new List<int>();
        public int TotalConstitutionLostAtDeath { get; set; }
        public double SpecLock { get; set; }

        #endregion

        #region IGamePlayer — Level / Class Properties

        public byte MaxLevel => 50;
        public int RealmLevel { get; set; }
        public int MLLevel { get; set; }
        public bool Champion { get; set; }
        public int ChampionLevel { get; set; }

        #endregion

        #region IGamePlayer — Guild

        private Guild _guild;

        public Guild Guild
        {
            get => _guild;
            set => _guild = value;
        }

        #endregion

        #region IGamePlayer — Duel System

        private GameDuel Duel { get; set; }

        public GameLiving DuelPartner => Duel?.GetPartnerOf(this);

        public void OnDuelStart(GameDuel duel)
        {
            Duel = duel;
        }

        public void OnDuelStop()
        {
            Duel = null;
        }

        public bool IsDuelPartner(GameLiving living)
        {
            return DuelPartner == living;
        }

        public bool IsDuelReady { get; set; }

        #endregion

        #region IGamePlayer — Shade System

        protected ShadeECSGameEffect m_ShadeEffect;
        private ushort _creationModel;

        public ShadeECSGameEffect ShadeEffect
        {
            get => m_ShadeEffect;
            set => m_ShadeEffect = value;
        }

        public bool IsShade => m_ShadeEffect != null || Model == ShadeModel;

        public ushort CreationModel => _creationModel == 0 ? Model : _creationModel;

        public ushort ShadeModel
        {
            get
            {
                if (CharacterClass != null && CharacterClass.ID == (int)eCharacterClass.Necromancer)
                    return 822;
                return Model;
            }
        }

        public virtual void Shade(bool state)
        {
            if (state)
            {
                if (m_ShadeEffect != null)
                    return;
                m_ShadeEffect = ECSGameEffectFactory.Create(
                    new(this, 0, 1),
                    static (in ECSGameEffectInitParams init) => new NecromancerShadeECSGameEffect(init)) as ShadeECSGameEffect;
                Model = ShadeModel;
            }
            else
            {
                ShadeECSGameEffect oldEffect = m_ShadeEffect;
                m_ShadeEffect = null;
                if (oldEffect?.IsActive == true)
                    _ = oldEffect.End();
                Model = CreationModel;
            }
        }

        #endregion

        #region IGamePlayer — Horse System

        public ControlledHorse ActiveHorse => null;

        private bool _isOnHorse;
        public virtual bool IsOnHorse
        {
            get => _isOnHorse;
            set => _isOnHorse = value;
        }

        /// <summary>
        /// Boards this bot on an existing, authoritative stable path.  A small
        /// optional delay lets player-led temporary companions visibly board in
        /// sequence after their real player while still using the same path and
        /// normal taxi movement.
        /// </summary>
        public bool BeginStableMasterRoute(
            string destination,
            TimeSpan expectedDuration,
            PathPoint route,
            DbItemTemplate ticket,
            int departureDelayMilliseconds = 0)
        {
            if (route == null || route.Next == null || route.Type is not EPathType.Once || CurrentRegion == null)
                return false;

            WakeRecoveryRest();
            CleanupStableRouteMount();
            StopAttack();
            StopFollowing();
            StopMovingOnPath();
            StopMoving();
            if (Brain is BotBrain brain)
                brain.ClearAggroList();
            // A player keeps their pet when boarding a horse (MountSteed releases nothing); so do the player's
            // companions, whose pet is placed beside them on arrival. Gamebots still release it.
            if (ControlledBrain != null && !IsTemporaryGroupHelper)
                ReleaseControlledPet(PetReleaseReason.StableTravel);
            AutonomousPetSupport.ReleaseFieldTurrets(this);

            // Unlike GamePlayer, GameBot is NPC-backed and cannot occupy the
            // native GameTaxi rider array. The taxi therefore owns the one and
            // only moving path while it synchronizes this bot's authoritative
            // server position. Running a second copy of the path on the bot was
            // the source of detached/riderless horses and arrival drift.
            var mount = ticket?.Color > 0
                ? new GameBotTaxi(this, NpcTemplateMgr.GetTemplate(ticket.Color))
                : new GameBotTaxi(this);
            mount.Realm = Realm;
            mount.X = route.X;
            mount.Y = route.Y;
            mount.Z = route.Z;
            mount.CurrentRegion = CurrentRegion;
            mount.Heading = route.GetHeading(route.Next);
            mount.FixedSpeed = true;
            mount.MaxSpeedBase = AutonomousStableRoutePlanner.StableSpeed;
            if (!mount.AddToWorld())
            {
                mount.Delete();
                return false;
            }

            _stableRouteMount = mount;
            _stableRouteMount.CurrentPathPoint = route;
            IsOnStableMasterRoute = true;
            IsOnHorse = true;
            StableRouteDestination = destination ?? string.Empty;
            _stableRouteExpectedArrivalUtc = DateTime.UtcNow + expectedDuration +
                                              TimeSpan.FromMilliseconds(Math.Max(0, departureDelayMilliseconds)) +
                                              TimeSpan.FromMinutes(2);
            _stableRouteLastMovementTick = GameLoop.GameLoopTime;
            _stableRouteLastPosition = new Point3D(route.X, route.Y, route.Z);
            _stableRouteStart = route;
            PathPoint endpoint = route;
            while (endpoint.Next != null)
                endpoint = endpoint.Next;
            _stableRouteEndpoint = new Point3D(endpoint.X, endpoint.Y, endpoint.Z);
            _stableRouteRecoveryCount = 0;
            CurrentPathPoint = null;
            mount.SynchronizeRider();
            RefreshStableRideViewers();
            _nextStableViewerRefreshTick = GameLoop.GameLoopTime + 2_000 + ObjectID % 750;
            if (departureDelayMilliseconds > 0)
            {
                _stableRouteDeparturePending = true;
                _stableRouteDepartureTimer = new ECSGameTimer(this, StartStableMasterRouteMotion);
                _stableRouteDepartureTimer.Start(departureDelayMilliseconds);
            }
            else
            {
                StartStableMasterRouteMotion(null);
            }
            if (PersistentRecord != null)
            {
                PersistentRecord.Activity = "Riding stablemaster horse route";
                PersistentRecord.CurrentGoal = $"Travel to {StableRouteDestination}";
                PersistentRecord.TravelDestination = StableRouteDestination;
            }
            AutonomousStuckWatchdog.MarkProgress(this, eAutonomousProgressKind.StableTravel);
            log.Info($"AUTONOMOUS_STABLE_ROUTE_BOARD bot=\"{Name}\" id={DatabaseID} level={Level} " +
                     $"realm={GlobalConstants.RealmToName(Realm)} destination=\"{StableRouteDestination}\" ticket=\"{ticket?.Name}\" " +
                     $"endpoint={_stableRouteEndpoint.X},{_stableRouteEndpoint.Y},{_stableRouteEndpoint.Z}");
            return true;
        }

        private int StartStableMasterRouteMotion(ECSGameTimer timer)
        {
            _stableRouteDepartureTimer?.Stop();
            _stableRouteDepartureTimer = null;
            _stableRouteDeparturePending = false;
            if (!IsOnStableMasterRoute)
                return 0;

            if (_stableRouteMount?.ObjectState is eObjectState.Active && _stableRouteMount.CurrentPathPoint != null)
                _stableRouteMount.MoveOnPath(AutonomousStableRoutePlanner.StableSpeed);
            _stableRouteLastMovementTick = GameLoop.GameLoopTime;
            return 0;
        }

        public bool IsProtectedStableMasterTravel(DateTime nowUtc) =>
            IsOnStableMasterRoute && IsOnHorse && nowUtc <= _stableRouteExpectedArrivalUtc;

        public void CompleteStableMasterRoute() => CompleteStableMasterRoute(false);

        private void CompleteStableMasterRoute(bool confirmedArrival)
        {
            bool wasStableTravel = IsOnStableMasterRoute;
            try { CompleteStableMasterRouteCore(confirmedArrival, wasStableTravel); }
            finally
            {
                // A companion's pet rejoins it at the end of the ride (it could not keep up with the horse).
                if (wasStableTravel && IsTemporaryGroupHelper && ControlledBrain?.Body is { IsAlive: true } pet &&
                    pet.ObjectState == eObjectState.Active && pet.CurrentRegionID == CurrentRegionID && pet.MaxSpeedBase > 0 &&
                    !IsWithinRadius(pet, 400))
                {
                    Point2D beside = GetPointFromHeading(Heading, 64);
                    pet.MoveInRegion(CurrentRegionID, beside.X, beside.Y, Z + 10, (ushort)((Heading + 2048) % 4096), false);
                }
            }
        }

        private void CompleteStableMasterRouteCore(bool confirmedArrival, bool wasStableTravel)
        {
            string completedDestination = StableRouteDestination;
            _stableRouteDepartureTimer?.Stop();
            _stableRouteDepartureTimer = null;
            _stableRouteDeparturePending = false;
            CleanupStableRouteMount();
            IsOnStableMasterRoute = false;
            IsOnHorse = false;
            StableRouteDestination = string.Empty;
            _stableRouteExpectedArrivalUtc = default;
            _stableRouteStart = null;
            _stableRouteEndpoint = null;
            _stableRouteRecoveryCount = 0;
            if (wasStableTravel && IsAutonomousWorldBot)
            {
                Vector3 arrival = new(X, Y, Z);
                // A horse lands on the ground. Route end points were recorded on another client's
                // terrain at some border keeps (Castle Sauvage ~150 units low, Svasud Faste ~450 units
                // high on the classic meshes), leaving the rider off the walkable floor; every route
                // then failed and the pocket escape sent it to its capital (164 Albion bots in 30
                // minutes, 2026-10-06). Same spot, real floor height.
                if ((AutonomousStableRoutePlanner.ShouldCorrectAuditedMularnLanding(
                         confirmedArrival, IsAlive, CurrentRegionID, arrival) &&
                     AutonomousRouteHotspotRepair.TryResolveFloor(PathfindingProvider.Instance,
                         CurrentZone, CurrentRegionID, arrival, out Vector3 floor) ||
                     IsAlive && AutonomousStableRoutePlanner.TryGroundLanding(PathfindingProvider.Instance, CurrentZone, arrival, out floor)) &&
                    MoveInRegion(CurrentRegionID, (int)Math.Round(floor.X), (int)Math.Round(floor.Y),
                        (int)Math.Round(floor.Z), Heading, true))
                    log.Info($"AUTONOMOUS_STABLE_LANDING_CORRECTION bot=\"{Name}\" id={DatabaseID} " +
                             $"region={CurrentRegionID} from={arrival} to={floor}");
                AutonomousStuckWatchdog.MarkProgress(this, eAutonomousProgressKind.StableTravel);
                log.Info($"AUTONOMOUS_STABLE_ROUTE_ARRIVE bot=\"{Name}\" id={DatabaseID} level={Level} " +
                         $"realm={GlobalConstants.RealmToName(Realm)} destination=\"{completedDestination}\" position={X},{Y},{Z}");
            }
        }

        /// <summary>
        /// A transient loss of movement flags is not an arrival. The rider stays
        /// mounted until the real final ticket waypoint has actually been
        /// reached and the Once path has released its last point.
        /// </summary>
        public bool TryCompleteStableMasterRouteAfterArrival()
        {
            if (!IsOnStableMasterRoute)
                return true;
            bool atEndpoint = _stableRouteEndpoint != null &&
                              DistanceSquared(X, Y, _stableRouteEndpoint.X, _stableRouteEndpoint.Y) <= 90L * 90L;
            if (!AutonomousStableRouteLifecycle.ShouldComplete(
                    _stableRouteDeparturePending,
                    _stableRouteMount?.IsMovingOnPath == true,
                    _stableRouteMount?.CurrentPathPoint != null,
                    atEndpoint))
                return false;
            CompleteStableMasterRoute(true);
            return true;
        }

        public void MaintainStableMasterRoute()
        {
            if (!IsOnStableMasterRoute)
                return;

            if (_stableRouteMount?.ObjectState is not eObjectState.Active)
            {
                log.Warn($"AUTONOMOUS_STABLE_ROUTE_FAIL bot=\"{Name}\" id={DatabaseID} level={Level} " +
                         $"realm={GlobalConstants.RealmToName(Realm)} destination=\"{StableRouteDestination}\" reason=\"authoritative taxi left world before arrival\"");
                CompleteStableMasterRoute();
                return;
            }

            if (_stableRouteMount is GameBotTaxi botTaxi)
                botTaxi.SynchronizeRider();
            if (GameServiceUtils.ShouldTick(_nextStableViewerRefreshTick))
            {
                _nextStableViewerRefreshTick = GameLoop.GameLoopTime + 2_000 + ObjectID % 750;
                RefreshStableRideViewers();
            }
            if (_stableRouteDeparturePending)
                return;
            if (DistanceSquared(X, Y, _stableRouteLastPosition.X, _stableRouteLastPosition.Y) >= 24L * 24L)
            {
                _stableRouteLastPosition = new Point3D(X, Y, Z);
                _stableRouteLastMovementTick = GameLoop.GameLoopTime;
                AutonomousStuckWatchdog.MarkProgress(this, eAutonomousProgressKind.StableTravel);
            }

            if (_stableRouteMount.CurrentPathPoint != null && !_stableRouteMount.IsMovingOnPath &&
                GameLoop.GameLoopTime - _stableRouteLastMovementTick >= 5_000)
            {
                _stableRouteMount.MoveOnPath(AutonomousStableRoutePlanner.StableSpeed);
                _stableRouteLastMovementTick = GameLoop.GameLoopTime;
            }

            if (_stableRouteMount.CurrentPathPoint == null && !_stableRouteMount.IsMovingOnPath && _stableRouteEndpoint != null &&
                DistanceSquared(X, Y, _stableRouteEndpoint.X, _stableRouteEndpoint.Y) > 90L * 90L &&
                GameLoop.GameLoopTime - _stableRouteLastMovementTick >= 1_000)
            {
                _stableRouteMount.CurrentPathPoint = FindStableRouteRecoveryPoint();
                if (_stableRouteMount.CurrentPathPoint != null)
                {
                    _stableRouteRecoveryCount++;
                    _stableRouteMount.MoveOnPath(AutonomousStableRoutePlanner.StableSpeed);
                    _stableRouteLastMovementTick = GameLoop.GameLoopTime;
                    log.Warn($"AUTONOMOUS_STABLE_ROUTE_RESUME bot={Name} id={DatabaseID} " +
                             $"destination=\"{StableRouteDestination}\" recovery={_stableRouteRecoveryCount} " +
                             $"position={X},{Y},{Z}");
                }
            }

            if (_stableRouteMount?.ObjectState is eObjectState.Active &&
                _stableRouteMount.CurrentPathPoint != null && !_stableRouteMount.IsMovingOnPath)
            {
                _stableRouteMount.MoveOnPath(AutonomousStableRoutePlanner.StableSpeed);
            }
        }

        private PathPoint FindStableRouteRecoveryPoint()
        {
            PathPoint nearest = null;
            long bestDistance = long.MaxValue;
            for (PathPoint point = _stableRouteStart; point != null; point = point.Next)
            {
                long distance = DistanceSquared(X, Y, point.X, point.Y);
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                nearest = point;
            }
            return bestDistance <= 120L * 120L && nearest?.Next != null ? nearest.Next : nearest;
        }

        internal void SynchronizeStableRoutePosition(GameBotTaxi mount)
        {
            if (mount == null || !IsOnStableMasterRoute || mount != _stableRouteMount ||
                mount.CurrentRegion != CurrentRegion)
                return;

            m_x = mount.X;
            m_y = mount.Y;
            m_z = mount.Z;
            Heading = mount.Heading;
            movementComponent.ForceUpdatePosition();
            SubZoneObject.CheckForRelocation();
        }

        internal GameTaxi StableRouteMountForClient =>
            IsOnStableMasterRoute && ObjectState is eObjectState.Active &&
            _stableRouteMount?.ObjectState is eObjectState.Active &&
            _stableRouteMount.CurrentRegion == CurrentRegion ? _stableRouteMount : null;

        private void RefreshStableRideViewers()
        {
            if (_stableRouteMount?.ObjectState is not eObjectState.Active)
                return;

            GamePlayer[] viewers = GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE)
                .Where(player => player?.Client?.ClientState is GameClient.eClientState.Playing &&
                                 player.IsWithinRadius(_stableRouteMount, WorldMgr.VISIBILITY_DISTANCE))
                .ToArray();
            HashSet<GamePlayer> present = viewers.ToHashSet();
            foreach (GamePlayer departed in _stableRideViewers.Keys.Where(player => !present.Contains(player)))
                _stableRideViewers.TryRemove(departed, out _);

            foreach (GamePlayer player in viewers)
                SendStableRideAssociation(player);
        }

        /// <summary>
        /// The native player path creates the horse before its rider. Do the same
        /// for NPC-backed bots without changing their server type or taxi movement.
        /// Reattach after recreation/position packets and retry at a bounded rate.
        /// </summary>
        internal void SendStableRideAssociation(GamePlayer player, bool force = false)
        {
            GameTaxi mount = StableRouteMountForClient;
            if (player?.Client?.ClientState is not GameClient.eClientState.Playing ||
                mount == null || !IsWithinRadius(player, WorldMgr.VISIBILITY_DISTANCE) ||
                !player.IsWithinRadius(mount, WorldMgr.VISIBILITY_DISTANCE) ||
                !IsVisibleTo(player) || !mount.IsVisibleTo(player) ||
                !player.CanDetect(this) || !player.CanDetect(mount))
                return;

            StableRideObserver observer = _stableRideViewers.GetOrAdd(player, static _ => new());
            observer.Refresh(GameLoop.GameLoopTime, force,
                () =>
                {
                    // Also refresh an already visible on-foot bot when it first boards.
                    // ClientService preserves equipment and its normal object caches.
                    ClientService.CreateObjectForPlayer(player, mount);
                    ClientService.CreateObjectForPlayer(player, this);
                },
                () =>
                {
                    if (StableRouteMountForClient == mount)
                        player.Out.SendRiding(this, mount, false);
                });
        }

        private void CleanupStableRouteMount()
        {
            if (_stableRouteMount != null)
            {
                foreach (GamePlayer player in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                    player.Out.SendRiding(this, _stableRouteMount, true);
                if (_stableRouteMount.ObjectState is eObjectState.Active)
                    _stableRouteMount.RemoveFromWorld();
                _stableRouteMount.Delete();
            }
            _stableRouteMount = null;
            _stableRideViewers.Clear();
            _nextStableViewerRefreshTick = 0;
            _stableRouteLastMovementTick = 0;
            if (ObjectState is eObjectState.Active)
                ClientService.UpdateNpcForPlayers(this, false);
        }

        protected override bool RetainCorpseForResurrection => true;
        protected override bool RetainGroupWhenDead => true;

        public override void ProcessDeath(GameObject killer)
        {
            AutonomousPetSupport.CancelPendingCharm(this);
            _deathTick = GameLoop.GameLoopTime;
            _deathRegionId = CurrentRegionID;
            _deathLocation = new Point3D(X, Y, Z);
            IsReturningAfterRelease = false;
            _stableReturnPlan = null;
            CompleteStableMasterRoute();

            if (IsAutonomousWorldBot && PersistentRecord != null)
            {
                PersistentRecord.DeathCount++;
                GoalDiagnosticAttempt?.Died();
                PersistentRecord.TargetName = (TargetObject as GameLiving)?.Name ?? killer?.Name ?? string.Empty;
                PersistentRecord.Activity = "Defeated; reassessing target difficulty";
                PersistentRecord.ObjectiveProgress = "The next grind target will be a lower con, never below green";
                MarkAutonomousStateDirty();
            }

            base.ProcessDeath(killer);
            if (IsAutonomousWorldBot)
                AutonomousBotStatusPersistence.Queue(this);
            StartDeathRecoveryTimer();
        }

        private void StartDeathRecoveryTimer()
        {
            _deathRecoveryTimer?.Stop();
            _deathRecoveryTimer = new ECSGameTimer(this)
            {
                Callback = HandleDeathRecovery
            };
            _deathRecoveryTimer.Start(3000);
        }

        private long _nextDeathRecoveryErrorTick;

        private int HandleDeathRecovery(ECSGameTimer timer)
        {
            if (ObjectState is not eObjectState.Active)
                return 0;
            if (IsAlive)
            {
                _deathRecoveryTimer = null;
                return 0;
            }

            long deadFor = GameLoop.GameLoopTime - _deathTick;
            AutonomousBotGroupCoordinator.PveCorpseDisposition groupDisposition =
                AutonomousBotGroupCoordinator.PveCorpseRecovery(this);
            if (groupDisposition == AutonomousBotGroupCoordinator.PveCorpseDisposition.HoldForResurrection)
                return 3000;
            bool resurrectionPossible = HasViableResurrector();
            long releaseDelay = resurrectionPossible ? 90_000 : 20_000;
            if (groupDisposition is not (AutonomousBotGroupCoordinator.PveCorpseDisposition.ReleaseAndDisband or
                AutonomousBotGroupCoordinator.PveCorpseDisposition.ReleaseAndRejoin) &&
                deadFor < releaseDelay)
                return 3000;

            // Leave the corpse and its retry timer intact while the owner is
            // dead or the party is still fighting. Reviving first would send
            // a one-third-health helper straight back into the corpse fight.
            if (IsTemporaryGroupHelper && !TemporaryCompanionRecovery.CanReleaseToOwner(this))
                return 3000;

            try
            {
                ReleaseToNearestBindAndReturn(groupDisposition !=
                    AutonomousBotGroupCoordinator.PveCorpseDisposition.ReleaseAndDisband);
            }
            catch (Exception exception)
            {
                // A transient transfer failure must not kill the only release timer.
                // Rate limit per character; no DB retry/sleep on the game thread.
                if (GameLoop.GameLoopTime >= _nextDeathRecoveryErrorTick)
                {
                    _nextDeathRecoveryErrorTick = GameLoop.GameLoopTime + 60_000;
                    log.Error($"BOT_RELEASE_RETRY bot={Name} id={DatabaseID} region={_deathRegionId}", exception);
                }
                return 3000;
            }
            if (!IsAlive)
                return 3000; // A failed destination/transfer must retain the recovery timer.
            _deathRecoveryTimer = null;
            return 0;
        }

        private bool HasViableResurrector()
        {
            if (Group == null)
                return false;

            foreach (GameLiving member in Group.GetMembersInTheGroup())
            {
                if (member == this || !member.IsAlive || member.CurrentRegionID != CurrentRegionID)
                    continue;
                if (member is GameBot bot && bot.ResurrectionSpell != null && bot.Mana >= bot.PowerCost(bot.ResurrectionSpell))
                    return true;
                if (member is GamePlayer player && player.CharacterClass?.ID is
                    (int)eCharacterClass.Cleric or (int)eCharacterClass.Friar or
                    (int)eCharacterClass.Healer or (int)eCharacterClass.Shaman or
                    (int)eCharacterClass.Druid or (int)eCharacterClass.Warden or (int)eCharacterClass.Bard)
                    return true;
            }
            return false;
        }

        private void ReleaseToNearestBindAndReturn(bool returnToParty = true)
        {
            if (IsTemporaryGroupHelper)
            {
                if (!TemporaryCompanionRecovery.CanReleaseToOwner(this))
                    return;
                // Move the corpse FIRST. Reviving before a failed MoveTo left a
                // living helper at its killer with an expired recovery timer.
                Vector3 destination = TemporaryGroupStableTravel.FormationPoint(Owner, CompanionRaid.IsMember(this) ? Math.Max(0, GroupIndex - 1) : ObjectID % 6);
                StopCurrentSpellcast();
                (Brain as BotBrain)?.PrepareForTankPull();
                StopMoving();
                if (!MoveTo(Owner.CurrentRegionID, (int)Math.Round(destination.X),
                    (int)Math.Round(destination.Y), (int)Math.Round(destination.Z), Owner.Heading))
                    return;

                Health = Math.Max(1, MaxHealth);
                Mana = Math.Max(0, MaxMana);
                Endurance = Math.Max(0, MaxEndurance);
                IsReturningAfterRelease = false;
                _stableReturnPlan = null;
                EnterPlayerLedGroup(Owner);
                Brain?.FSM?.SetCurrentState(eFSMStateType.FOLLOW);
                Brain?.Start();
                return;
            }

            Point3D release = FindNearestBindPoint();
            ushort releaseRegion = _deathRegionId;
            if (IsAutonomousWorldBot && release != null)
            {
                Zone releaseZone = WorldMgr.GetRegion(releaseRegion)?.GetZone(release.X, release.Y);
                if (releaseZone == null || !AutonomousRealmBoundary.Allows(Realm, releaseRegion, releaseZone.ID))
                    release = null;
            }
            if (release == null && IsAutonomousWorldBot)
            {
                var capital = AutonomousStuckWatchdog.SafeCapitalFor(Realm);
                releaseRegion = capital.RegionId;
                release = new Point3D(capital.X, capital.Y, capital.Z);
            }
            release ??= _deathLocation;
            // Transfer the corpse first: a failed MoveTo must not revive the bot
            // at its killer or cause HandleDeathRecovery to discard its timer.
            if (!MoveTo(releaseRegion, release.X, release.Y, release.Z, Heading))
                return;
            Health = Math.Max(1, MaxHealth / 3);
            Mana = Math.Max(0, MaxMana / 3);
            Endurance = Math.Max(0, MaxEndurance / 3);
            IsReturningAfterRelease = returnToParty;
            if (returnToParty) AutonomousRealmRaid.RejoinAfterRelease(this);
            _nextReturnRoleplayTick = GameLoop.GameLoopTime + Util.Random(45_000, 90_000);
            Brain?.FSM?.SetCurrentState(eFSMStateType.FOLLOW);
            Brain?.Start();
            if (returnToParty)
                Say(ReturnDialogue("release"));

            if (PersistentRecord != null)
            {
                PersistentRecord.Activity = returnToParty ? "Released and returning on foot" : "Released after party disbanded";
                if (returnToParty)
                    PersistentRecord.CurrentGoal = $"Return to the party near {_deathLocation.X}, {_deathLocation.Y}";
                PersistentRecord.TravelDestination = CurrentZone?.Description ?? string.Empty;
                MarkAutonomousStateDirty();
            }
        }

        private Point3D? FindNearestBindPoint()
        {
            return BotReleaseBindPoints.Nearest(_deathRegionId, X, Y, IsAutonomousWorldBot ? Realm : eRealm.None);
        }

        public void OnResurrectedBy(GameLiving caster)
        {
            AutonomousRealmRaid.ClearCorpseWait(this);
            _deathRecoveryTimer?.Stop();
            _deathRecoveryTimer = null;
            IsReturningAfterRelease = false;
            _stableReturnPlan = null;
            CompleteStableMasterRoute();
            if (IsTemporaryGroupHelper)
                TryReturnTemporaryCompanionToLeader(true);
            Brain?.FSM?.SetCurrentState(eFSMStateType.FOLLOW);
            Brain?.Start();
            Say(Random.Shared.Next(3) switch
            {
                0 => $"My thanks, {caster?.Name ?? "friend"}. I am ready to continue.",
                1 => "That was too close. Give me a moment, then I will fall back into formation.",
                _ => "Back among the living. I will stay nearer the group this time."
            });
        }

        private long _nextCompanionRecallTick;

        public bool TryReturnTemporaryCompanionToLeader(bool afterRevival = false)
        {
            // Even persistent bots in a human's party retain their walk-back.
            if (!IsTemporaryGroupHelper || !IsAlive || Owner?.IsAlive != true ||
                Owner.ObjectState != eObjectState.Active || Owner.CurrentZone == null)
                return false;
            if ((afterRevival || IsReturningAfterRelease) && !TemporaryCompanionRecovery.CanReleaseToOwner(this))
                return false;
            if (!afterRevival && GameLoop.GameLoopTime < _nextCompanionRecallTick)
                return false;
            _nextCompanionRecallTick = GameLoop.GameLoopTime + TemporaryCompanionRecovery.CheckIntervalMilliseconds;
            bool sameGroup = Group != null && Group == Owner.Group && Group.IsInTheGroup(this) && Group.IsInTheGroup(Owner);
            if (!TemporaryCompanionRecovery.ShouldRecall(true, IsAlive, ObjectState == eObjectState.Active,
                sameGroup, PlayerGroupLeader == Owner, IsOnStableMasterRoute, Owner.IsOnHorse,
                CurrentRegionID == Owner.CurrentRegionID, GetDistanceTo(Owner), afterRevival || IsReturningAfterRelease))
                return false;

            // A combat knockback/dragon throw must not recall and reset an entire raid.
            // Genuine portal transfers use the separate transfer coordinator.
            if (DragonCombatGeometry.IsRecoveringFromThrow(Owner) ||
                CurrentRegionID == Owner.CurrentRegionID && TemporaryCompanionRecovery.HasPartyCombat(this) &&
                !TemporaryCompanionRecovery.FrontierChaseLeash(this))
                return false;
            // A raid squad on a keep siege holds its post until the event ends (goal 11: no leash during sieges).
            if (CurrentRegionID == Owner.CurrentRegionID && CompanionRaidSiege.Holds(this))
                return false;

            Vector3 destination = TemporaryGroupStableTravel.FormationPoint(Owner, CompanionRaid.IsMember(this) ? Math.Max(0, GroupIndex - 1) : ObjectID % 6);
            StopCurrentSpellcast();
            WakeRecoveryRest();
            (Brain as BotBrain)?.PrepareForTankPull();
            StopMoving();
            if (!MoveTo(Owner.CurrentRegionID, (int)Math.Round(destination.X), (int)Math.Round(destination.Y),
                (int)Math.Round(destination.Z), Owner.Heading))
                return false;

            IsReturningAfterRelease = false;
            _stableReturnPlan = null;
            EnterPlayerLedGroup(Owner);
            Brain?.Start();
            return true;
        }

        // The autonomous controller owns released solo and group journeys.
        // Human-led companions retain their existing return-to-player behavior.
        public void HandOverReleaseReturnToGroup()
        {
            if (!IsAutonomousWorldBot || IsTemporaryGroupHelper || IsPlayerLedGroup ||
                !IsAlive || !IsReturningAfterRelease || IsOnStableMasterRoute)
                return;
            IsReturningAfterRelease = false;
            _stableReturnPlan = null;
            StopMovingOnPath();
            StopMoving();
        }

        public bool ContinueReturnJourney()
        {
            if (!IsReturningAfterRelease || !IsAlive)
                return false;

            if (IsTemporaryGroupHelper)
            {
                TryReturnTemporaryCompanionToLeader(true);
                return IsReturningAfterRelease;
            }

            GamePlayer leader = PlayerGroupLeader ?? Owner;
            Point3D destination = leader?.IsAlive == true && leader.CurrentRegionID == CurrentRegionID
                ? new Point3D(leader.X, leader.Y, leader.Z)
                : _deathLocation;
            if (CurrentRegionID != _deathRegionId && (leader == null || leader.CurrentRegionID != CurrentRegionID))
                return true;

            if (GetDistanceTo(destination) <= 350)
            {
                IsReturningAfterRelease = false;
                _stableReturnPlan = null;
                CompleteStableMasterRoute();
                Say(ReturnDialogue("arrive"));
                Brain?.FSM?.SetCurrentState(eFSMStateType.FOLLOW);
                return false;
            }

            if (IsOnStableMasterRoute)
            {
                MaintainStableMasterRoute();
                if (IsMovingOnPath || CurrentPathPoint != null)
                    return true;
                if (!TryCompleteStableMasterRouteAfterArrival())
                    return true;
                _stableReturnPlan = null;
                Say(ReturnDialogue("dismount"));
            }

            _stableReturnPlan ??= FindFasterStableRoute(destination);
            if (_stableReturnPlan != null)
            {
                if (!IsWithinRadius(_stableReturnPlan.Master, 225))
                {
                    WalkReturnStep(new Point3D(_stableReturnPlan.Master.X, _stableReturnPlan.Master.Y, _stableReturnPlan.Master.Z));
                    MaybeExplainReturn("stable");
                    return true;
                }

                if (DistanceSquared(X, Y, _stableReturnPlan.Route.X, _stableReturnPlan.Route.Y) > 45L * 45L)
                {
                    WalkReturnStep(new Point3D(_stableReturnPlan.Route.X, _stableReturnPlan.Route.Y, _stableReturnPlan.Route.Z));
                    return true;
                }

                long price = Math.Max(0, _stableReturnPlan.Ticket.Price);
                if (price > 0 && !AutonomousBotEconomy.TrySpend(DatabaseID, price))
                {
                    _stableReturnPlan = null;
                    return true;
                }
                if (!BeginStableMasterRoute(_stableReturnPlan.DestinationName,
                        TimeSpan.FromSeconds(_stableReturnPlan.RideSeconds), _stableReturnPlan.Route, _stableReturnPlan.Ticket))
                {
                    if (price > 0)
                        AutonomousBotEconomy.AddMoney(DatabaseID, price);
                    _stableReturnPlan = null;
                    return true;
                }
                _stableReturnPlan = null;
                Say(ReturnDialogue("mount"));
                return true;
            }

            WalkReturnStep(destination);
            MaybeExplainReturn("walk");
            return true;
        }

        private void WalkReturnStep(Point3D destination)
        {
            Vector3 current = new(X, Y, Z);
            Vector3 desired = new(destination.X, destination.Y, destination.Z);
            AutonomousThreatAwarePathing.SafeStep step = AutonomousThreatAwarePathing.ChooseStep(this, current, desired, 900);
            PathTo(step.Position, MaxSpeed);
            if (PersistentRecord != null)
            {
                PersistentRecord.Activity = step.Detouring ? step.Reason : "Walking back to the party";
                PersistentRecord.CurrentGoal = "Return to party after release";
                MarkAutonomousStateDirty();
            }
        }

        private AutonomousStableRoutePlanner.Choice FindFasterStableRoute(Point3D destination)
        {
            return CurrentRegion == null
                ? null
                : AutonomousStableRoutePlanner.FindBest(this, new Vector3(destination.X, destination.Y, destination.Z));
        }

        private void MaybeExplainReturn(string stage)
        {
            if (GameLoop.GameLoopTime < _nextReturnRoleplayTick || IsCasting || InCombat)
                return;
            _nextReturnRoleplayTick = GameLoop.GameLoopTime + Util.Random(60_000, 120_000);
            Say(ReturnDialogue(stage));
        }

        private string ReturnDialogue(string stage) => stage switch
        {
            "release" => Random.Shared.Next(3) switch
            {
                0 => "No resurrection is coming. I have released, and I am making my way back.",
                1 => "I woke at the bindstone. Hold the road; I will return on foot.",
                _ => "The healers could not reach me. I am alive again and starting the journey back."
            },
            "stable" => "The stable is on my route. A horse should save time from here.",
            "mount" => $"I found a faster horse route toward {StableRouteDestination}. I am taking it now.",
            "dismount" => "Off the horse. I will cover the rest of the road on foot.",
            "arrive" => "I have caught up. Returning to formation.",
            _ => Random.Shared.Next(4) switch
            {
                0 => "Still on the road. I am avoiding the stronger creatures along the way.",
                1 => "I know the direction now. Keep going; I will catch up.",
                2 => "The road is longer than it looked from the bindstone, but I am making progress.",
                _ => $"I am crossing {CurrentZone?.Description ?? "the wilds"} and heading back to the group."
            }
        };

        private static long DistanceSquared(int ax, int ay, int bx, int by)
        {
            long dx = (long)ax - bx;
            long dy = (long)ay - by;
            return dx * dx + dy * dy;
        }

        #endregion

        #region IGamePlayer — Sprint

        public bool IsSprinting => effectListComponent.ContainsEffectForEffectType(eEffect.Sprint);

        public virtual bool Sprint(bool state)
        {
            if (state == IsSprinting)
                return state;

            if (state)
            {
                if (Endurance <= 10 || IsStealthed || !IsAlive)
                    return false;

                ECSGameEffectFactory.Create(new ECSGameEffectInitParams(this, 0, 1),
                    static (in ECSGameEffectInitParams i) => new SprintECSGameEffect(i));
                return true;
            }
            else
            {
                ECSGameEffect effect = EffectListService.GetEffectOnTarget(this, eEffect.Sprint);
                effect?.End();
                return false;
            }
        }

        #endregion

        #region IGamePlayer — Status Flags

        public bool CanBreathUnderWater { get; set; }
        public bool IsOverencumbered { get; set; }
        public int Encumberance => 0;
        public int MaxEncumberance => 0;

        #endregion

        #region IGamePlayer — Skill System

        protected readonly Dictionary<string, Specialization> m_specialization = new Dictionary<string, Specialization>();
        protected readonly List<SpellLine> m_spellLines = new List<SpellLine>();

        public virtual bool HasSpecialization(string keyName)
        {
            lock (((ICollection)m_specialization).SyncRoot)
            {
                return m_specialization.ContainsKey(keyName);
            }
        }

        public virtual SpellLine GetSpellLine(string keyname)
        {
            lock (m_spellLines)
            {
                return m_spellLines.FirstOrDefault(sl => sl.KeyName == keyname);
            }
        }

        #endregion

        #region IGamePlayer — Equipment / Armor

        public virtual eArmorSlot CalculateArmorHitLocation(AttackData ad)
        {
            int random = Util.Random(99);
            if (random < 35) return eArmorSlot.TORSO;
            if (random < 55) return eArmorSlot.LEGS;
            if (random < 70) return eArmorSlot.ARMS;
            if (random < 82) return eArmorSlot.HEAD;
            if (random < 91) return eArmorSlot.HAND;
            return eArmorSlot.FEET;
        }

        public double WeaponDamageWithoutQualityAndCondition(DbInventoryItem weapon)
        {
            if (weapon == null)
                return 0;

            double weaponDps = weapon.DPS_AF * 0.1;
            double dpsCap = 1.2 + 0.3 * Level;
            if (RealmLevel > 39)
                dpsCap += 0.3;

            double dps = Math.Min(weaponDps, dpsCap);
            return dps * (1 + GetModified(eProperty.DPS) * 0.01);
        }

        public override int GetWeaponStat(DbInventoryItem weapon)
        {
            if (weapon != null)
            {
                switch ((eObjectType)weapon.Object_Type)
                {
                    case eObjectType.Staff:
                    case eObjectType.Fired:
                    case eObjectType.Longbow:
                    case eObjectType.Crossbow:
                    case eObjectType.CompositeBow:
                    case eObjectType.RecurvedBow:
                    case eObjectType.Thrown:
                    case eObjectType.Shield:
                        return GetModified(eProperty.Dexterity);

                    case eObjectType.ThrustWeapon:
                    case eObjectType.Piercing:
                    case eObjectType.Spear:
                    case eObjectType.Flexible:
                    case eObjectType.HandToHand:
                        return (GetModified(eProperty.Strength) + GetModified(eProperty.Dexterity)) >> 1;
                }
            }

            return GetModified(eProperty.Strength);
        }

        public override double GetWeaponSkill(DbInventoryItem weapon)
        {
            if (weapon == null || CharacterClass == null)
                return 0;

            int classBaseWeaponSkill = (eInventorySlot)weapon.SlotPosition is eInventorySlot.DistanceWeapon
                ? CharacterClass.WeaponSkillRangedBase
                : CharacterClass.WeaponSkillBase;
            double weaponSkill = Level * classBaseWeaponSkill / 200.0 *
                                 (1 + 0.01 * GetWeaponStat(weapon) / 2) * Effectiveness;
            return Math.Max(1, weaponSkill * GetModified(eProperty.WeaponSkill) * 0.01);
        }

        public override int WeaponSpecLevel(eObjectType objectType, int slotPosition)
        {
            if (objectType is eObjectType.LeftAxe && slotPosition is not Slot.LEFTHAND)
                objectType = eObjectType.Axe;
            else if (slotPosition is Slot.LEFTHAND && objectType is eObjectType.Axe)
                objectType = eObjectType.LeftAxe;

            string specName = SkillBase.ObjectTypeToSpec(objectType);
            return string.IsNullOrWhiteSpace(specName) ? 0 : GetModifiedSpecLevel(specName);
        }

        public override int WeaponSpecLevel(DbInventoryItem weapon) =>
            weapon == null ? 0 : WeaponSpecLevel((eObjectType)weapon.Object_Type, weapon.SlotPosition);

        #endregion

        #region IGamePlayer — XP / Realm Points

        public object GameStaticItemOwnerComparand => DatabaseID > 0 ? $"offlinebot:{DatabaseID}" : this;

        public TryPickUpResult TryAutoPickUpMoney(GameMoney money)
        {
            money.AssertLockAcquisition();
            if (!IsAutonomousWorldBot || IsTemporaryGroupHelper || DatabaseID <= 0 ||
                !AutonomousBotEconomy.AddMoney(DatabaseID, money.Value))
                return TryPickUpResult.DoesNotWant;

            money.RemoveFromWorld();
            AutonomousStuckWatchdog.MarkProgress(this, eAutonomousProgressKind.Loot);
            MarkAutonomousStateDirty();
            return TryPickUpResult.Success;
        }

        private string _reportedLootRejection;

        public TryPickUpResult TryAutoPickUpItem(WorldInventoryItem item)
        {
            item.AssertLockAcquisition();
            if (!IsAutonomousWorldBot || IsTemporaryGroupHelper)
                return TryPickUpResult.DoesNotWant;
            if (Inventory == null)
            {
                ReportLootRejection(item, "missing_inventory");
                return TryPickUpResult.DoesNotWant;
            }

            bool stored = item.Item.IsStackable
                ? Inventory.AddTemplate(item.Item, item.Item.Count, eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack)
                : Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, item.Item);
            if (!stored)
            {
                ReportLootRejection(item, Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack) == eInventorySlot.Invalid
                    ? "backpack_full" : "inventory_insert_failed");
                return TryPickUpResult.Blocked;
            }

            item.RemoveFromWorld();
            _reportedLootRejection = null;
            AutonomousBotEconomy.MarkInventoryChanged(this);
            // Queue owned loot for the existing asynchronous save before appraisal.
            // This is not a synchronous commit; /spawn helpers never enter this path.
            MarkAutonomousStateDirty();
            AutonomousBotStatusPersistence.Queue(this, true);
            bool equipped = false;
            try
            {
                equipped = !item.Item.IsStackable && AutonomousBotEconomy.TryEquipOwnedUpgrade(this, item.Item, "loot");
            }
            catch (Exception exception)
            {
                // Receipt already succeeded. An appraisal failure must not make
                // the drop available twice or prevent the owned item being saved.
                const string appraisalFailure = "autonomous.telemetry.loot.appraisal_failed";
                if (!TempProperties.GetProperty<bool>(appraisalFailure))
                {
                    TempProperties.SetProperty(appraisalFailure, true);
                    if (log.IsErrorEnabled)
                        log.Error($"AUTONOMOUS_LOOT_APPRAISAL_FAILED bot=\"{Name}\" id={DatabaseID} item=\"{item.Item.Name}\" retainedInInventory=true", exception);
                }
            }
            AutonomousStuckWatchdog.MarkProgress(this, eAutonomousProgressKind.Loot);
            const string firstLootTelemetry = "autonomous.telemetry.first.loot";
            if (!TempProperties.GetProperty<bool>(firstLootTelemetry))
            {
                TempProperties.SetProperty(firstLootTelemetry, true);
                if (log.IsInfoEnabled)
                    log.Info($"AUTONOMOUS_FIRST_LOOT bot=\"{Name}\" id={DatabaseID} level={Level} " +
                         $"item=\"{item.Item.Name}\" count={item.Item.Count} equipped={equipped}");
            }
            return TryPickUpResult.Success;
        }

        private void ReportLootRejection(WorldInventoryItem item, string reason)
        {
            // One line until the rejection changes or a later pickup succeeds.
            // A full backpack remains full: never delete existing loot to make room.
            if (_reportedLootRejection == reason)
                return;
            _reportedLootRejection = reason;
            if (log.IsInfoEnabled)
                log.Info($"AUTONOMOUS_LOOT_REJECTED bot=\"{Name}\" id={DatabaseID} level={Level} " +
                     $"item=\"{item.Item.Name}\" reason={reason} dropRetained=true");
        }

        public TryPickUpResult TryPickUpMoney(GamePlayer source, GameMoney money) => TryPickUpResult.DoesNotWant;
        public TryPickUpResult TryPickUpItem(GamePlayer source, WorldInventoryItem item) => TryPickUpResult.DoesNotWant;

        public void AddXPGainer(GameObject xpGainer, float damageAmount)
        {
        }

        public void GainRealmPoints(long amount, bool modify)
        {
            if (!IsAutonomousWorldBot || amount <= 0)
                return;
            AutonomousRealmPoints += amount;
            RealmLevel = Math.Max(RealmLevel, AutonomousBotRealmPointRewards.RealmLevelFor(AutonomousRealmPoints));
            AutonomousStuckWatchdog.MarkProgress(this, eAutonomousProgressKind.RealmPoints);
            MarkAutonomousStateDirty();
        }

        public override void GainRealmPoints(long amount) => GainRealmPoints(amount, true);

        public override void GainExperience(GainedExperienceEventArgs arguments, bool notify = true)
        {
            if (!IsAutonomousWorldBot || arguments == null || arguments.ExpTotal <= 0 || Level >= MaxLevel)
                return;

            long experienceGained = arguments.ExpTotal;
            long baseExperience = arguments.ExpBase;
            if (arguments.AllowMultiply)
            {
                experienceGained -= baseExperience;

                if (ServerProperties.Properties.ENABLE_ZONE_BONUSES && CurrentZone != null)
                {
                    long zoneBonus = baseExperience * CurrentZone.BonusExperience / 100;
                    if (zoneBonus > 0)
                        experienceGained += ScaleAutonomousExperience(zoneBonus, false);
                }

                baseExperience = ScaleAutonomousExperience(baseExperience,
                    CurrentRegion?.IsRvR == true || CurrentZone?.IsRvR == true);

                long itemExperienceBonus = GetModified(eProperty.XpPoints);
                if (itemExperienceBonus != 0)
                    baseExperience += baseExperience * itemExperienceBonus / 100;

                experienceGained += baseExperience;
            }

            if (experienceGained <= 0)
                return;

            long previousExperience = Experience;
            byte previousLevel = Level;
            Experience += experienceGained;
            AutonomousStuckWatchdog.MarkProgress(this, eAutonomousProgressKind.Experience);
            bool leveled = false;
            while (Level < MaxLevel && Experience >= GamePlayer.GetExperienceAmountForLevel(Level))
            {
                Level++;
                ApplyLevelStatGrowth(Level);
                leveled = true;
            }

            if (leveled)
            {
                // New specialization points remain unspent until the bot chooses
                // to reach and interact with its real class trainer.
                RefreshSpecDependantSkills(false);
                SetBotSpells();
                SortStyles();
                SortSpells();
                Health = MaxHealth;
                Mana = MaxMana;
                Endurance = MaxEndurance;
                AutonomousBotStatusPersistence.Queue(this, true);
            }
            else
            {
                MarkAutonomousStateDirty();
                // XP is a real progression event, so coalesce it into the next
                // status batch instead of waiting for a server-wide save.
                AutonomousBotStatusPersistence.Queue(this);
            }
            if (previousExperience == 0 || previousLevel != Level)
            {
                log.Info($"AUTONOMOUS_XP_PROGRESS bot=\"{Name}\" id={DatabaseID} class=\"{ClassName}\" " +
                         $"amount={experienceGained} xp={previousExperience}->{Experience} level={previousLevel}->{Level}");
            }
        }

        private static long ScaleAutonomousExperience(long experience, bool isRvR)
        {
            double rate = ServerProperties.Properties.BOT_XP_RATE;
            if (isRvR)
                rate *= ServerProperties.Properties.RvR_XP_RATE;
            return (long)(experience * rate);
        }

        #endregion

        #region IGamePlayer — Stealth

        public override void Stealth(bool goStealth)
        {
            if (IsStealthed == goStealth)
                return;

            if (goStealth)
            {
                // Songs and pulsing spells are mutually exclusive with stealth.
                // Keep the same legality rule without using GamePlayer-only effects.
                if (effectListComponent.ContainsEffectForEffectType(eEffect.Pulse))
                    return;

                if (IsOnHorse)
                    IsOnHorse = false;

                Flags |= eFlags.STEALTH;
                OnMaxSpeedChange();
                // Vanish from enemy players who cannot detect it (GamePlayer.CanDetect), as a stealthing player does.
                foreach (GamePlayer viewer in GetPlayersInRadius(WorldMgr.VISIBILITY_DISTANCE))
                    if (!viewer.CanDetect(this))
                        viewer.Out.SendObjectRemove(this);
                return;
            }

            Flags &= ~eFlags.STEALTH;
            OnMaxSpeedChange();
        }

        public void StartStealthUncoverAction()
        {
        }

        public void StopStealthUncoverAction()
        {
        }

        #endregion

        #region IGamePlayer — Pet Control

        public void CommandNpcRelease()
        {
            ReleaseControlledPet(PetReleaseReason.Voluntary);
        }

        private enum PetReleaseReason { Voluntary, StableTravel, WorldRemoval }

        private void ReleaseControlledPet(PetReleaseReason reason)
        {
            // AI maintenance and companion-level upgrades cannot recycle a
            // living Necromancer servant. Required travel/world teardown is
            // explicit so this guard cannot leak pets or trap a shade.
            if (reason == PetReleaseReason.Voluntary && IsAlive &&
                ObjectState == eObjectState.Active &&
                ControlledBrain?.Body is NecromancerPet { IsAlive: true, ObjectState: eObjectState.Active })
                return;

            AutonomousPetSupport.CancelPendingCharm(this);
            bool necromancerPetDied = false;
            if (ControlledBrain != null)
            {
                var brain = ControlledBrain;
                GameNPC petBody = brain.Body;
                if (petBody is NecromancerPet servant)
                    servant.BotReleaseReason = !servant.IsAlive ? "death" : reason.ToString();
                necromancerPetDied = CharacterClass?.ID == (int)eCharacterClass.Necromancer &&
                                      petBody != null && !petBody.IsAlive;
                bool syntheticCharm = petBody?.TempProperties.GetProperty<bool>(AutonomousPetSupport.SyntheticCharmPetProperty) == true;
                // Let the summon effect perform its normal release cleanup.
                // This removes caster/pet effects and, for a Bonedancer
                // commander, releases every sub-pet before deleting the body.
                if (brain is ControlledMobBrain controlledBrain)
                    controlledBrain.OnRelease();
                else if (brain.Body != null && brain.Body != this)
                    brain.Body.Delete();
                if (ControlledBrain == brain)
                    RemoveControlledBrain(brain);
                if (syntheticCharm && petBody?.ObjectState is eObjectState.Active)
                    petBody.Delete();
            }
            if (CharacterClass?.ID == (int)eCharacterClass.Necromancer && IsShade)
            {
                Shade(false);

                // In classic 1.65 a Necromancer survives the loss of its
                // zombie only barely. Match that failure state for autonomous
                // and temporary bots, while leaving normal voluntary releases
                // and stable-travel despawns untouched.
                if (necromancerPetDied && IsAlive)
                    Health = 1;
            }
        }

        public override bool RemoveControlledBrain(IControlledBrain controlledBrain)
        {
            // PetECSGameEffect removes the controlled brain before deleting a
            // dead summon. GameBot does not use GamePlayer's character-class
            // callback, so without this hook a Necromancer bot could remain an
            // invulnerable orphaned shade until a later AI maintenance pulse.
            bool deadNecromancerPet = CharacterClass?.ID == (int)eCharacterClass.Necromancer &&
                                      controlledBrain?.Body is NecromancerPet { IsAlive: false };

            bool removed = base.RemoveControlledBrain(controlledBrain);
            if (!removed || CharacterClass?.ID != (int)eCharacterClass.Necromancer)
                return removed;

            if (IsShade)
                Shade(false);
            if (deadNecromancerPet && IsAlive)
                Health = 1;
            return true;
        }

        #endregion

        #region IGamePlayer — TargetInView

        protected bool m_targetInView = true;

        public override bool TargetInView
        {
            get
            {
                if (TargetObject != null && GetDistanceTo(TargetObject) <= TargetInViewAlwaysTrueMinRange)
                    return true;
                return m_targetInView;
            }
            set => m_targetInView = value;
        }

        public override int TargetInViewAlwaysTrueMinRange =>
            (TargetObject is GamePlayer targetPlayer && targetPlayer.IsMoving) ? 100 : 64;

        protected bool _groundTargetInView = true;

        public override bool GroundTargetInView
        {
            get => _groundTargetInView;
            set => _groundTargetInView = value;
        }

        #endregion

        #region Combat Styles and Spell Lists

        public new List<Style> StylesChain { get; protected set; }
        public new List<Style> StylesDefensive { get; protected set; }
        public new List<Style> StylesBack { get; protected set; }
        public new List<Style> StylesSide { get; protected set; }
        public new List<Style> StylesFront { get; protected set; }
        public new List<Style> StylesAnytime { get; protected set; }
        public List<Style> StylesTaunt { get; protected set; }
        public List<Style> StylesDetaunt { get; protected set; }
        public List<Style> StylesShield { get; protected set; }

        public List<Spell> InstantCrowdControlSpells { get; set; }
        public List<Spell> CrowdControlSpells { get; set; }
        public List<Spell> BoltSpells { get; set; }

        public bool CanUseSideStyles => StylesSide != null && StylesSide.Count > 0;
        public bool CanUseBackStyles => StylesBack != null && StylesBack.Count > 0;
        public bool CanUseFrontStyle => StylesFront != null && StylesFront.Count > 0;
        public bool CanUseAnytimeStyles => StylesAnytime != null && StylesAnytime.Count > 0;
        public bool CanUsePositionalStyles => CanUseSideStyles || CanUseBackStyles;
        public bool CanCastCrowdControlSpells => CrowdControlSpells != null && CrowdControlSpells.Count > 0;
        public bool CanCastBolts => BoltSpells != null && BoltSpells.Count > 0;

        // Heal spell properties
        public Spell HealBig { get; protected set; }
        public Spell HealEfficient { get; protected set; }
        public Spell HealGroup { get; protected set; }
        public Spell HealInstant { get; protected set; }
        public Spell HealInstantGroup { get; protected set; }
        public Spell HealOverTime { get; protected set; }
        public Spell HealOverTimeGroup { get; protected set; }
        public Spell HealOverTimeInstant { get; protected set; }
        public Spell HealOverTimeInstantGroup { get; protected set; }
        public Spell CureMezz { get; protected set; }
        public Spell CureDisease { get; protected set; }
        public Spell CureDiseaseGroup { get; protected set; }
        public Spell CurePoison { get; protected set; }
        public Spell CurePoisonGroup { get; protected set; }
        public Spell ResurrectionSpell { get; protected set; }

        protected const int HEALTH_REGEN_PERIOD = 6000;

        public static double HealAmount(Spell spell, GameLiving target)
        {
            return spell.Value >= 0
                ? spell.Value
                : target.MaxHealth * spell.Value * -0.01d;
        }

        public int PowerCost(Spell spell) => BotSpellPower.Cost(this, spell, null);

        private Dictionary<int, SpellLine> _powerSpellLines = new();

        // Combat still uses its existing dispatch line (no damage/spec behavior
        // changes). Power alone resolves the learned line for staff focus. The
        // cache is rebuilt with the spell list, not searched on every AI turn.
        internal SpellLine ResolvePowerSpellLine(Spell spell, SpellLine supplied)
        {
            if (supplied != null && supplied.KeyName != GlobalSpellsLines.Mob_Spells)
                return supplied;
            return spell != null && _powerSpellLines != null && _powerSpellLines.TryGetValue(spell.ID, out SpellLine learned)
                ? learned : supplied;
        }

        public override void SortSpells()
        {
            if (Spells.Count < 1)
                return;

            InstantHarmfulSpells?.Clear();
            HarmfulSpells?.Clear();
            InstantHealSpells?.Clear();
            HealSpells?.Clear();
            InstantCrowdControlSpells?.Clear();
            CrowdControlSpells?.Clear();
            BoltSpells?.Clear();
            InstantMiscSpells?.Clear();
            MiscSpells?.Clear();

            HealBig = null;
            HealEfficient = null;
            HealGroup = null;
            HealInstant = null;
            HealInstantGroup = null;
            HealOverTime = null;
            HealOverTimeGroup = null;
            HealOverTimeInstant = null;
            HealOverTimeInstantGroup = null;
            CureMezz = null;
            CureDisease = null;
            CureDiseaseGroup = null;
            CurePoison = null;
            CurePoisonGroup = null;
            ResurrectionSpell = null;

            foreach (Spell spell in Spells)
            {
                if (spell == null || AutonomousPetSupport.IsDisabledBotDamageShield(this, spell) ||
                    !AnimistSingleTargetPolicy.AllowsAutomatedSpell(this, spell))
                    continue;

                if (spell.SpellType == eSpellType.Resurrect)
                {
                    if (ResurrectionSpell == null || spell.Level > ResurrectionSpell.Level ||
                        spell.Level == ResurrectionSpell.Level && spell.ResurrectHealth > ResurrectionSpell.ResurrectHealth)
                        ResurrectionSpell = spell;
                }
                else if (spell.SpellType == eSpellType.Bolt)
                {
                    BoltSpells ??= new List<Spell>(1);
                    BoltSpells.Add(spell);
                }
                else if (spell.SpellType == eSpellType.Mesmerize ||
                        (spell.SpellType == eSpellType.SpeedDecrease && spell.Value >= 99))
                {
                    CrowdControlSpells ??= new List<Spell>(1);
                    CrowdControlSpells.Add(spell);
                }
                else if (spell.IsHarmful)
                {
                    if (spell.IsInstantCast)
                    {
                        InstantHarmfulSpells ??= new List<Spell>(1);
                        InstantHarmfulSpells.Add(spell);
                    }
                    else
                    {
                        HarmfulSpells ??= new List<Spell>(1);
                        HarmfulSpells.Add(spell);
                    }
                }
                else if (spell.IsHealing && !spell.IsPulsing && !spell.IsConcentration)
                {
                    if (spell.Target == eSpellTarget.PET)
                        continue;

                    double valueNew = HealAmount(spell, this);

                    if (spell.SpellType == eSpellType.CureMezz)
                        CureMezz = spell;
                    else if (spell.SpellType == eSpellType.CureDisease)
                    {
                        if ((spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            && (CureDiseaseGroup == null || spell.Duration > CureDiseaseGroup.Duration))
                            CureDiseaseGroup = spell;
                        else if (spell.Target == eSpellTarget.REALM && (CureDisease == null || spell.Duration > CureDisease.Duration))
                            CureDisease = spell;
                    }
                    else if (spell.SpellType == eSpellType.CurePoison)
                    {
                        if ((spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            && (CurePoisonGroup == null || spell.Duration > CurePoisonGroup.Duration))
                            CurePoisonGroup = spell;
                        else if (spell.Target == eSpellTarget.REALM && (CurePoison == null || spell.Duration > CurePoison.Duration))
                            CurePoison = spell;
                    }
                    else if (spell.IsInstantCast)
                    {
                        InstantHealSpells ??= new List<Spell>(1);
                        InstantHealSpells.Add(spell);

                        if (spell.SpellType == eSpellType.HealOverTime || spell.SpellType == eSpellType.HealthRegenBuff)
                        {
                            if (spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            {
                                if (HealOverTimeInstantGroup == null)
                                    HealOverTimeInstantGroup = spell;
                                else
                                {
                                    double perSecondOld = HealOverTimeInstantGroup.SpellType == eSpellType.HealOverTime
                                        ? HealAmount(HealOverTimeInstantGroup, this) / HealOverTimeInstantGroup.Frequency
                                        : HealAmount(HealOverTimeInstantGroup, this) / HEALTH_REGEN_PERIOD / 2;
                                    double perSecondNew = spell.SpellType == eSpellType.HealOverTime
                                        ? valueNew / spell.Frequency
                                        : valueNew / HEALTH_REGEN_PERIOD / 2;

                                    if (perSecondNew > perSecondOld)
                                        HealOverTimeInstantGroup = spell;
                                }
                            }
                            else if (spell.Target == eSpellTarget.REALM)
                            {
                                if (HealOverTimeInstant == null)
                                    HealOverTimeInstant = spell;
                                else
                                {
                                    double perSecondOld = HealOverTimeInstant.SpellType == eSpellType.HealOverTime
                                        ? HealAmount(HealOverTimeInstant, this) / HealOverTimeInstant.Frequency
                                        : HealAmount(HealOverTimeInstant, this) / HEALTH_REGEN_PERIOD / 2;
                                    double perSecondNew = spell.SpellType == eSpellType.HealOverTime
                                        ? valueNew / spell.Frequency
                                        : valueNew / HEALTH_REGEN_PERIOD / 2;

                                    if (perSecondNew > perSecondOld)
                                        HealOverTimeInstant = spell;
                                }
                            }
                        }
                        else if (spell.SpellType == eSpellType.Heal)
                        {
                            if (spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            {
                                if (HealInstantGroup == null || valueNew > HealAmount(HealInstantGroup, this))
                                    HealInstantGroup = spell;
                            }
                            else if (spell.Target == eSpellTarget.REALM)
                            {
                                if (HealInstant == null || valueNew > HealAmount(HealInstant, this))
                                    HealInstant = spell;
                            }
                        }
                    }
                    else
                    {
                        HealSpells ??= new List<Spell>(1);
                        HealSpells.Add(spell);

                        if (spell.SpellType == eSpellType.HealOverTime || spell.SpellType == eSpellType.HealthRegenBuff)
                        {
                            if (spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            {
                                if (HealOverTimeGroup == null)
                                    HealOverTimeGroup = spell;
                                else
                                {
                                    double perSecondOld = HealOverTimeGroup.SpellType == eSpellType.HealOverTime
                                        ? HealAmount(HealOverTimeGroup, this) / HealOverTimeGroup.Frequency
                                        : HealAmount(HealOverTimeGroup, this) / HEALTH_REGEN_PERIOD / 2;
                                    double perSecondNew = spell.SpellType == eSpellType.HealOverTime
                                        ? valueNew / spell.Frequency
                                        : valueNew / HEALTH_REGEN_PERIOD / 2;

                                    if (perSecondNew > perSecondOld)
                                        HealOverTimeGroup = spell;
                                }
                            }
                            else if (spell.Target == eSpellTarget.REALM)
                            {
                                if (HealOverTime == null)
                                    HealOverTime = spell;
                                else
                                {
                                    double perSecondOld = HealOverTime.SpellType == eSpellType.HealOverTime
                                        ? HealAmount(HealOverTime, this) / HealOverTime.Frequency
                                        : HealAmount(HealOverTime, this) / HEALTH_REGEN_PERIOD / 2;
                                    double perSecondNew = spell.SpellType == eSpellType.HealOverTime
                                        ? valueNew / spell.Frequency
                                        : valueNew / HEALTH_REGEN_PERIOD / 2;

                                    if (perSecondNew > perSecondOld)
                                        HealOverTime = spell;
                                }
                            }
                        }
                        else if (spell.SpellType == eSpellType.Heal)
                        {
                            if (spell.Target == eSpellTarget.GROUP || spell.Radius > 0)
                            {
                                if (HealGroup == null || valueNew / spell.CastTime > HealAmount(HealGroup, this) / HealGroup.CastTime)
                                    HealGroup = spell;
                            }
                            else if (spell.Target == eSpellTarget.REALM)
                            {
                                if (HealEfficient == null)
                                    HealEfficient = spell;
                                else
                                {
                                    double perSecondNew = valueNew / spell.CastTime;
                                    double perSecondEff = HealAmount(HealEfficient, this) / HealEfficient.CastTime;
                                    double perSecondBig = HealBig == null ? 0.0 : HealAmount(HealBig, this) / HealBig.CastTime;

                                    double effNew = valueNew / PowerCost(spell) + 0.25;
                                    double effOld = HealAmount(HealEfficient, this) / PowerCost(HealEfficient);

                                    if (effNew > effOld)
                                    {
                                        if (perSecondEff > perSecondNew && perSecondEff > perSecondBig)
                                            HealBig = HealEfficient;

                                        HealEfficient = spell;
                                    }
                                    else if (perSecondNew > perSecondEff && perSecondNew > perSecondBig)
                                        HealBig = spell;
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (spell.IsInstantCast && spell.SpellType != eSpellType.SpeedEnhancement)
                    {
                        InstantMiscSpells ??= new List<Spell>(1);
                        InstantMiscSpells.Add(spell);
                    }
                    else
                    {
                        MiscSpells ??= new List<Spell>(1);
                        MiscSpells.Add(spell);
                    }
                }
            }
        }

        public override void SortStyles()
        {
            StylesChain?.Clear();
            StylesDefensive?.Clear();
            StylesBack?.Clear();
            StylesSide?.Clear();
            StylesFront?.Clear();
            StylesAnytime?.Clear();
            StylesTaunt?.Clear();
            StylesDetaunt?.Clear();
            StylesShield?.Clear();

            if (Styles == null)
                return;

            foreach (Style s in Styles)
            {
                if (s == null)
                    continue;

                if (s.WeaponTypeRequirement != (int)eObjectType.Shield ||
                    s.WeaponTypeRequirement == (int)eObjectType.Shield && s.OpeningRequirementType == Style.eOpening.Defensive)
                {
                    switch (s.OpeningRequirementType)
                    {
                        case Style.eOpening.Defensive:
                            StylesDefensive ??= new List<Style>(1);
                            StylesDefensive.Add(s);
                            break;

                        case Style.eOpening.Positional:
                            switch ((Style.eOpeningPosition)s.OpeningRequirementValue)
                            {
                                case Style.eOpeningPosition.Back:
                                    StylesBack ??= new List<Style>(1);
                                    StylesBack.Add(s);
                                    break;

                                case Style.eOpeningPosition.Side:
                                    StylesSide ??= new List<Style>(1);
                                    StylesSide.Add(s);
                                    break;

                                case Style.eOpeningPosition.Front:
                                    StylesFront ??= new List<Style>(1);
                                    StylesFront.Add(s);
                                    break;
                            }
                            break;

                        default:
                            if (s.OpeningRequirementValue > 0)
                            {
                                StylesChain ??= new List<Style>(1);
                                StylesChain.Add(s);
                            }
                            else
                            {
                                bool added = false;

                                if (s.Procs.Count > 0)
                                {
                                    foreach (StyleProcInfo proc in s.Procs)
                                    {
                                        if (proc.Spell.SpellType == eSpellType.StyleTaunt)
                                        {
                                            if (proc.Spell.ID == 20000)
                                            {
                                                StylesTaunt ??= new List<Style>(1);
                                                StylesTaunt.Add(s);
                                                added = true;
                                            }
                                            else if (proc.Spell.ID == 20001)
                                            {
                                                StylesDetaunt ??= new List<Style>(1);
                                                StylesDetaunt.Add(s);
                                                added = true;
                                            }
                                        }
                                    }
                                }

                                if (!added)
                                {
                                    StylesAnytime ??= new List<Style>(1);
                                    StylesAnytime.Add(s);
                                }
                            }
                            break;
                    }
                }
                else
                {
                    StylesShield ??= new List<Style>(1);
                    StylesShield.Add(s);
                }
            }
        }

        #endregion

        #region Constructor

        public GameBot(
            GamePlayer owner,
            byte classId,
            string name = null,
            byte raceId = 0,
            byte genderId = 0,
            bool temporaryGroupHelper = false,
            byte equipmentLevel = 0,
            byte botLevel = 0)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            IsTemporaryGroupHelper = temporaryGroupHelper;
            ClassId = classId;
            RaceId = raceId;
            GenderId = genderId;
            ClassName = BotManager.GetClassNameById(classId);
            InternalID = Guid.NewGuid().ToString();
            _dummyClient = new BotDummyClient();
            _dummyLib = new BotDummyPacketLib();

            // Set class identity first — needed for stat init and specs
            if (!SetCharacterClass(ClassId))
                throw new InvalidOperationException($"Failed to set character class for class ID {ClassId}. Ensure the class ID is valid.");
            SetRaceAndRealm(owner);
            Level = BotAttributeProgression.InitialLevel(owner.Level, botLevel, IsTemporaryGroupHelper);
            _creationModel = Model;

            Name = string.IsNullOrEmpty(name) ? $"{owner.Name}'s {ClassName} Bot" : name;
            MaxSpeedBase = PLAYER_BASE_SPEED;

            // Stat and spec initialization — order matters
            InitializeBotStats();
            eCharacterClass temporaryClass = (eCharacterClass)ClassId;
            BotSpec = temporaryClass == eCharacterClass.Bonedancer
                ? new BonedancerBotSpec(BotSpec.ChooseRandomSpecialization(temporaryClass), 0, false)
                : temporaryClass == eCharacterClass.Sluaghbinder
                    ? new SluaghbinderBotSpec(BotSpec.ChooseRandomSpecialization(temporaryClass), 0, false)
                : BotSpec.GetSpec(temporaryClass, BotSpec.ChooseRandomSpecialization(temporaryClass));
            LoadClassSpecializations(false);
            SpendSpecPoints(Level, 0);
            RefreshSpecDependantSkills(false);
            SetBotSpells();
            SortStyles();
            SortSpells();
            EquipBot(equipmentLevel);

            Health = MaxHealth;
            Endurance = MaxEndurance;
            Mana = MaxMana;

            RespawnInterval = -1;

            // Set up BotBrain — replaces old BotAI system
            var brain = temporaryClass == eCharacterClass.Sluaghbinder
                ? new DOL.AI.Brain.SluaghbinderBotBrain()
                : new DOL.AI.Brain.BotBrain();
            brain.IsHealer = IsHealerClass();
            SetOwnBrain(brain);

            // ControlledBrain is reserved for an actual class pet. The bot's own
            // combat AI belongs in Brain via SetOwnBrain.
            InitControlledBrainArray(1);

            GameEventMgr.AddHandler(Owner, GamePlayerEvent.Quit, new DOLEventHandler(OnOwnerQuit));
            if (IsTemporaryGroupHelper)
                GameEventMgr.AddHandler(Owner, GamePlayerEvent.RegionChanged, new DOLEventHandler(OnOwnerRegionChanged));
        }

        /// <summary>
        /// Loads an ownerless autonomous character from its persistent record.
        /// No generated equipment and no field auto-training are allowed here.
        /// </summary>
        public GameBot(OfflineWorldBotRecord record) : this(record, null)
        {
        }

        public GameBot(OfflineWorldBotRecord record, System.Collections.IList preparedInventory)
        {
            PersistentRecord = record ?? throw new ArgumentNullException(nameof(record));
            AutonomousBotGoalPolicy.ReconcileSavedAssignment(record);
            // Autonomous Group instances are process-local and are rebuilt
            // after every server start. Never expose a former process's leader,
            // roster, or absolute meetup deadline while this actor is loading.
            if (record.ItineraryJson?.StartsWith(AutonomousBotGroupCoordinator.MetadataPrefix,
                    StringComparison.Ordinal) == true)
            {
                record.ItineraryJson = string.Empty;
                record.Dirty = true;
            }
            IsAutonomousWorldBot = true;
            DatabaseID = record.BotId;
            InternalID = AutonomousBotEconomy.GetOwnerId(record.BotId);
            ClassId = (byte)record.ClassId;
            RaceId = (byte)record.RaceId;
            GenderId = (byte)record.Gender;
            ClassName = string.IsNullOrWhiteSpace(record.ClassName) ? BotManager.GetClassNameById(ClassId) : record.ClassName;
            _dummyClient = new BotDummyClient();
            _dummyLib = new BotDummyPacketLib();
            if (!SetCharacterClass(ClassId))
                throw new InvalidOperationException($"Failed to load autonomous class ID {ClassId} for {record.Name}.");
            SetRaceAndRealm(null);
            Level = (byte)Math.Clamp(record.Level, 1, MaxLevel);
            _creationModel = Model;
            if ((eRealm)record.Realm != eRealm.None)
                Realm = (eRealm)record.Realm;
            Name = record.Name;
            MaxSpeedBase = PLAYER_BASE_SPEED;

            InitializeBotStats();
            eCharacterClass persistentClass = (eCharacterClass)ClassId;
            BotSpec = persistentClass switch
            {
                eCharacterClass.Bonedancer => new BonedancerBotSpec(
                    BotSpec.ChoosePersistentSpecialization(persistentClass, record.BotId), record.BotId, true),
                eCharacterClass.Savage => new SavageBotSpec(
                    BotSpec.ChoosePersistentSpecialization(persistentClass, record.BotId),
                    SavageBotSpec.WeaponFromPersistedSpecs(record.SerializedSpecs)),
                eCharacterClass.Sluaghbinder => new SluaghbinderBotSpec(
                    BotSpec.ChoosePersistentSpecialization(persistentClass, record.BotId), record.BotId, true),
                _ => BotSpec.GetSpec(persistentClass,
                    BotSpec.ChoosePersistentSpecialization(persistentClass, record.BotId)),
            };
            LoadClassSpecializations(false);
            LoadPersistedSpecs(record.SerializedSpecs);
            bool buildLocked = BotLifetimeBuild.Restore(BotSpec, record.SerializedBuildPlan);
            if (!buildLocked)
                BotLifetimeBuild.AlignWithInvestedWeapons(BotSpec, GetBaseSpecLevel);
            // A saved build can predate the Savage's trained weapon line. Its
            // old weapon field must not override the specialization restored
            // from the character record.
            bool savagePlanCorrected = persistentClass == eCharacterClass.Savage &&
                SavageBotSpec.AlignWithPersistedSpecs(BotSpec, record.SerializedSpecs);
            if (!buildLocked || savagePlanCorrected)
            {
                record.SerializedBuildPlan = BotLifetimeBuild.Encode(BotSpec);
                MarkAutonomousStateDirty();
            }
            m_leftOverSpecPoints = Math.Max(0, record.UnspentSpecPoints);
            _lastAutonomousTrainedLevel = ParseLastTrainedLevel(record.SerializedAbilities, Level, record.SerializedSpecs);
            RefreshSpecDependantSkills(false);
            SetBotSpells();
            SortStyles();
            SortSpells();

            Inventory = new BotInventory(InternalID);
            if (preparedInventory == null)
                Inventory.LoadFromDatabase(InternalID);
            else
                Inventory.LoadInventory(InternalID, preparedInventory);
            bool starterWeaponAdded = EnsureAutonomousStarterWeapon();
            starterWeaponAdded |= BotRangedCombat.EnsureStarter(this);
            starterWeaponAdded |= BotStarterInstruments.Ensure(this);
            RefreshItemBonuses();
            if (starterWeaponAdded || !buildLocked)
                AutonomousBotStatusPersistence.Queue(this, starterWeaponAdded);
            Experience = Math.Max(0, record.Experience);
            AutonomousRealmPoints = Math.Max(0, record.RealmPoints);
            RealmLevel = AutonomousBotRealmPointRewards.RealmLevelFor(AutonomousRealmPoints);
            AutonomousBotRealmAbilities.Restore(this, ParseTrainedRealmPoints(record.SerializedAbilities));
            CurrentRegionID = (ushort)Math.Clamp(record.RegionId, 0, ushort.MaxValue);
            X = record.X;
            Y = record.Y;
            Z = record.Z;
            Health = Math.Clamp(record.Health, 1, MaxHealth);
            Mana = Math.Clamp(record.Mana, 0, MaxMana);
            Endurance = Math.Clamp(record.Endurance, 0, MaxEndurance);
            RespawnInterval = -1;

            var brain = persistentClass == eCharacterClass.Sluaghbinder
                ? new DOL.AI.Brain.SluaghbinderBotBrain { IsHealer = IsHealerClass() }
                : new DOL.AI.Brain.BotBrain { IsHealer = IsHealerClass() };
            SetOwnBrain(brain);
            InitControlledBrainArray(1);
        }

        private bool IsHealerClass()
        {
            return CharacterClass != null && BotPartyRoles.IsHealingClass((eCharacterClass)CharacterClass.ID);
        }

        #endregion

        #region Bot Management

        private void OnOwnerQuit(DOLEvent e, object sender, EventArgs arguments)
        {
            if (IsTemporaryGroupHelper)
                Delete();
            else
                BotManager.RemoveBot(this);
        }

        private void OnOwnerRegionChanged(DOLEvent e, object sender, EventArgs arguments)
        {
            if (IsTemporaryGroupHelper && sender is GamePlayer player)
                TemporaryGroupStableTravel.RelocateHelpersAfterPlayerTransfer(player);
        }

        public bool EnterPlayerLedGroup(GamePlayer leader)
        {
            if (leader == null || leader.Realm != Realm)
                return false;

            PlayerGroupLeader = leader;
            if (Brain is BotBrain brain)
            {
                brain.RebindAssistedPlayer();
                brain.FSM.SetCurrentState(eFSMStateType.FOLLOW);
            }
            return true;
        }

        public void LeavePlayerLedGroup()
        {
            if (IsTemporaryGroupHelper)
                CompleteStableMasterRoute();
            PlayerGroupLeader = null;
            if (Brain is BotBrain brain)
                brain.RebindAssistedPlayer();
        }

        private bool _recoveryRestLocked;
        private long _autonomousRecoveryStartTick;

        internal long AutonomousRecoveryStartTick => _autonomousRecoveryStartTick;

        /// <summary>
        /// Temporary companions expose their general recovery lock to the
        /// player-led coordination layer. The underlying recovery state is also
        /// used by autonomous playerbots and never changes actor presentation.
        /// </summary>
        public bool IsTemporaryCompanionRestLocked =>
            IsTemporaryGroupHelper && _recoveryRestLocked;

        public bool BeginRecoveryRest()
        {
            if (!IsAlive || IsOnStableMasterRoute)
                return false;

            if (_recoveryRestLocked)
                return true;

            // Harmless mobile songs are allowed to continue during logical
            // recovery. Ordinary casts are cancelled so recovery still owns
            // movement and combat activity.
            if ((IsCasting || castingComponent?.HasPendingSkillRequests == true) &&
                !BotSongTwistPolicy.HasMobileSongCast(this))
                StopCurrentSpellcast();
            if (IsAttacking)
                StopAttack();
            StopFollowing();
            StopMovingOnPath();
            StopMoving();
            _recoveryRestLocked = true;
            // GameBots remain standing NPC actors. Recovery has no IsSitting,
            // emote, player packet, model swap, or client-facing stance state.
            UpdateHealthManaEndu();
            StartEnhancedRecoveryTimers();
            return true;
        }

        public bool BeginTemporaryCompanionRest()
        {
            return IsTemporaryGroupHelper && BeginRecoveryRest();
        }

        public void WakeTemporaryCompanionRest()
        {
            if (IsTemporaryGroupHelper)
                WakeRecoveryRest();
        }

        public void WakeRecoveryRest()
        {
            _recoveryRestLocked = false;
        }

        /// <summary>
        /// Clears logical recovery as soon as this bot, its controlled pets, or
        /// a nearby member of its actual group enters combat. Support-only bots
        /// therefore heal and buff normally instead of retaining fast recovery
        /// while their party fights.
        /// </summary>
        public bool WakeRecoveryRestIfCombatBlocked()
        {
            if (!IsRecoveryResting || !BotRestRecovery.BlocksRest(this))
                return false;
            WakeRecoveryRest();
            return true;
        }

        // Wake only when an actual affordable spell is requested, not during
        // spell selection. Mobile song maintenance remains compatible with
        // out-of-combat logical recovery.
        public override bool CastSpell(Spell spell, SpellLine line, ISpellCastingAbilityHandler spellCastingAbilityHandler = null, bool checkLos = true)
        {
            if (CompanionFollowPolicy.DeferBuff(this, spell)) return false;
            // Final boundary as well as selection filtering: no rank of the
            // disabled shield can keep a Cabalist/Enchanter bot channeling.
            if (AutonomousPetSupport.IsDisabledBotDamageShield(this, spell) ||
                BotSpellPower.BlocksAttackerRotation(this, spell) ||
                !AnimistSingleTargetPolicy.AllowsAutomatedSpell(this, spell))
            {
                CabalistRestDiagnostics.Attempt(this, spell, false);
                return false;
            }

            if (spell == null || !CanAffordConcentration(spell) || Mana < BotSpellPower.Cost(this, spell, line) ||
                castingComponent.HasPendingSkillRequests ||
                BotSpellPower.IsOffensiveCaster(this) && castingComponent.IsCasting)
            {
                CabalistRestDiagnostics.Attempt(this, spell, false);
                return false;
            }

            // Song maintenance may continue while recovering and must not wake
            // a resting companion. A real, otherwise-valid buff, pet summon, or
            // combat cast is an intentional action and wakes it exactly once.
            if (IsTemporaryCompanionRestLocked)
            {
                if (!BotSongTwistPolicy.IsMobileSong(this, spell))
                {
                    (Brain as BotBrain)?.MarkTemporaryCompanionRestActivity();
                    WakeTemporaryCompanionRest();
                }
            }
            else if (IsTemporaryGroupHelper && !BotSongTwistPolicy.IsMobileSong(this, spell))
                (Brain as BotBrain)?.MarkTemporaryCompanionRestActivity();

            // Quickcast is explicitly disabled for all bot kinds. Clear a
            // lingering externally applied effect without changing human casts.
            EffectListService.GetAbilityEffectOnTarget(this, eEffect.QuickCast)?.End();

            if (IsRecoveryResting && !IsTemporaryGroupHelper &&
                !BotSongTwistPolicy.IsMobileSong(this, spell))
                WakeRecoveryRest();
            bool started = base.CastSpell(spell, line, spellCastingAbilityHandler, checkLos);
            CabalistRestDiagnostics.Attempt(this, spell, started);
            if (started && CharacterClass?.ID == (int)eCharacterClass.Animist)
                AnimistSingleTargetPolicy.CastOwnerSpell(this, spell);
            return started;
        }

        public override bool CastSpell(Spell spell, SpellLine line, bool checkLos)
        {
            return CastSpell(spell, line, null, checkLos);
        }

        public override void StartAttack(GameObject target)
        {
            // Verified damage/group-threat paths clear the lock before asking
            // the bot to attack. Reject stale AI/pet work that arrives while a
            // parked party is still recovering.
            if (target != null && IsRecoveryResting)
                return;

            // A ranged shot owns its aim/release animation.  Reissuing the
            // same target every think pulse used to reset that state, producing
            // repeated bow preparation with no completed shot.  Suppress only
            // duplicate GameBot requests while a real ranged action is active;
            // a new target or a completed/cleared state still starts normally.
            if (target != null && ActiveWeaponSlot == eActiveWeaponSlot.Distance &&
                attackComponent.AttackState &&
                rangeAttackComponent.RangedAttackState != eRangedAttackState.None &&
                (TargetObject == target || rangeAttackComponent.AutoFireTarget == target))
                return;

            base.StartAttack(target);
        }

        public override bool AddToWorld()
        {
            if (!base.AddToWorld())
                return false;

            RandomNumberDeck = new PlayerDeck();
            // An intentional MoveTo keeps both registrations alive across the
            // brief remove/add cycle. Re-registering here reset watchdog clocks
            // and dirtied the persistent record on every region transition.
            if (IsAutonomousWorldBot && !IsIntentionalWorldMove)
            {
                if (_autonomousRecoveryStartTick <= 0)
                    _autonomousRecoveryStartTick = GameLoop.GameLoopTime;
                AutonomousBotRegistry.Register(this);
                AutonomousStuckWatchdog.Register(this);
            }
            return true;
        }

        /// <summary>
        /// Generic NPC movement temporarily removes and re-adds the object.
        /// Keep an autonomous character registered as online across that
        /// intentional transfer so population state and watchdog clocks survive.
        /// The ordinary RemoveFromWorld pet release is retained, allowing the
        /// pet manager to re-summon a legal pet in the destination region.
        /// </summary>
        public override bool MoveTo(ushort regionID, int x, int y, int z, ushort heading)
        {
            // Owner 2026-10-07: travelling the old frontiers, the pets of the player's /spawn and /raid companions died
            // on every move (the move removes the bot from the world, which released its pet) and their pet classes kept
            // re-summoning. Within a region a companion's pet now comes along and is placed behind it, as a player's pet
            // is on a teleport (GamePlayer.MoveTo). Across regions pets are released as for players. Gamebots unchanged.
            GameNPC carriedPet = IsTemporaryGroupHelper && regionID == CurrentRegionID && ControlledBrain?.Body is { IsAlive: true } body &&
                body.ObjectState == eObjectState.Active ? body : null;
            if (carriedPet == null)
                return MoveToCore(regionID, x, y, z, heading);
            _keepPetThroughMove++;
            bool result;
            try { result = MoveToCore(regionID, x, y, z, heading); }
            finally { _keepPetThroughMove--; }
            if (result && ControlledBrain?.Body == carriedPet && carriedPet.IsAlive && carriedPet.ObjectState == eObjectState.Active &&
                carriedPet.MaxSpeedBase > 0)
            {
                Point2D behind = GetPointFromHeading(Heading, 64);
                carriedPet.MoveInRegion(CurrentRegionID, behind.X, behind.Y, Z + 10, (ushort)((Heading + 2048) % 4096), false);
            }
            return result;
        }

        private int _keepPetThroughMove;

        private bool MoveToCore(ushort regionID, int x, int y, int z, ushort heading)
        {
            if (!IsAutonomousWorldBot)
                return base.MoveTo(regionID, x, y, z, heading);

            if (regionID != CurrentRegionID)
            {
                AutonomousPetSupport.CancelPendingCharm(this);
                AutonomousPetSupport.ReleaseFieldTurrets(this);
            }

            bool moved = false;
            BeginIntentionalWorldMove();
            try
            {
                moved = base.MoveTo(regionID, x, y, z, heading);
                return moved;
            }
            finally
            {
                bool outermostMove = EndIntentionalWorldMove();

                // GameObject.MoveTo removes the actor before AddToWorld tries
                // the destination.  If that final add fails, the ordinary
                // removal callback was intentionally suppressed and the old
                // registry entry would otherwise remain forever: the launcher
                // showed an "Offline" bot as online, the objective timer kept
                // advancing, and the population controller could not reload it.
                // A rejected move which leaves the actor active is harmless and
                // stays in-world for the caller to replan.
                if (outermostMove && ObjectState != eObjectState.Active)
                {
                    AutonomousStuckWatchdog.Unregister(this);
                    AutonomousBotRegistry.Unregister(this);
                    if (PersistentRecord != null)
                    {
                        PersistentRecord.IsOnline = false;
                        PersistentRecord.Activity = "Queued after a failed world transfer";
                        PersistentRecord.ObjectiveProgress =
                            $"Region transfer to {regionID} was rejected after world removal; waiting for a clean reload";
                        PersistentRecord.LastUpdateUtc = DateTime.UtcNow.ToString("O");
                        PersistentRecord.Dirty = true;
                        MarkAutonomousStateDirty();
                        AutonomousBotStatusPersistence.Queue(this);
                    }
                    log.Warn($"AUTONOMOUS_TRANSFER_REQUEUE bot={Name} id={DatabaseID} " +
                             $"target_region={regionID} moved={moved}");
                }
            }
        }

        public override void Delete()
        {
            if (ObjectState is eObjectState.Deleted)
                return;

            // Group.RemoveMember owns deletion of temporary helpers after its
            // bookkeeping finishes. If Delete was the entry point, allow that
            // one nested call to finish the object and do not clean it twice.
            if (Group != null)
            {
                Group group = Group;
                group.RemoveMember(this);
                if (ObjectState is eObjectState.Deleted)
                    return;
            }

            _deathRecoveryTimer?.Stop();
            _deathRecoveryTimer = null;
            AutonomousPetSupport.CancelPendingCharm(this);
            AutonomousPetSupport.ReleaseFieldTurrets(this);
            CompleteStableMasterRoute();
            if (IsAutonomousWorldBot && !IsIntentionalWorldMove)
                SaveAutonomousState(true);
            if (Owner != null)
            {
                GameEventMgr.RemoveHandler(Owner, GamePlayerEvent.Quit, new DOLEventHandler(OnOwnerQuit));
                if (IsTemporaryGroupHelper)
                    GameEventMgr.RemoveHandler(Owner, GamePlayerEvent.RegionChanged, new DOLEventHandler(OnOwnerRegionChanged));
            }
            base.Delete();
        }

        public override bool RemoveFromWorld()
        {
            // Capture this before base.RemoveFromWorld stops the brain. The stop
            // path is allowed to invoke virtual methods and must never change how
            // this removal is classified after it has begun.
            bool intentionalWorldMove = IsIntentionalWorldMove;
            if (!intentionalWorldMove)
                TempProperties.GetProperty<GameRelic>(GameRelic.PLAYER_CARRY_RELIC_WEAK)?.DropFromCarrier(this);
            if (IsAutonomousWorldBot && !intentionalWorldMove)
                SaveAutonomousState(true);
            // A charm cast can have created its tagged candidate before the
            // ownership effect installs ControlledBrain. World removal must
            // clean that pending body even during this short handshake.
            AutonomousPetSupport.CancelPendingCharm(this);
            if (!base.RemoveFromWorld())
                return false;

            Duel?.Stop();

            if (ControlledBrain != null && _keepPetThroughMove == 0)
                ReleaseControlledPet(PetReleaseReason.WorldRemoval);

            if (IsAutonomousWorldBot && !intentionalWorldMove)
            {
                AutonomousStuckWatchdog.Unregister(this);
                AutonomousBotRegistry.Unregister(this);
                if (PersistentRecord != null)
                {
                    PersistentRecord.IsOnline = false;
                    PersistentRecord.Activity = "Offline";
                    PersistentRecord.LastUpdateUtc = DateTime.UtcNow.ToString("O");
                    PersistentRecord.Dirty = true;
                    GameServer.Database.SaveObject(PersistentRecord);
                }
            }

            return true;
        }

        public override void OnAttackedByEnemy(AttackData ad)
        {
            // Notify BotBrain of the attack so it can add aggro and transition to combat state
            if (Brain is BotBrain botBrain)
                botBrain.OnAttackedByEnemy(ad);

            // A defensive class pet may retaliate when its owner is attacked,
            // while remaining non-aggressive on ordinary pulls.
            if (ControlledBrain is ControlledMobBrain controlledBrain)
                controlledBrain.OnOwnerAttacked(ad);

            BotBrain.NotifyNearbyGroupBots(this, ad);
            base.OnAttackedByEnemy(ad);
        }

        #endregion

        #region Race and Realm

        private void SetRaceAndRealm(GamePlayer owner)
        {
            if (CharacterClass == null)
                return;

            var eligibleRaces = CharacterClass.EligibleRaces;
            eGender gender = GenderId == (byte)eGender.Female ? eGender.Female : eGender.Male;

            if (RaceId != 0 && eligibleRaces != null)
            {
                // Use the user-provided race if it's eligible for this class
                var requestedRace = eligibleRaces.FirstOrDefault(r => (byte)r.ID == RaceId);
                if (requestedRace != null)
                {
                    Race = (short)requestedRace.ID;
                    Model = (ushort)requestedRace.GetModel(gender);
                    Gender = gender;
                }
                else
                {
                    // Provided race not eligible — fall back to random
                    PlayerRace playerRace = eligibleRaces[Util.Random(eligibleRaces.Count - 1)];
                    Race = (short)playerRace.ID;
                    Model = (ushort)playerRace.GetModel(gender);
                    Gender = gender;
                }
            }
            else if (eligibleRaces != null && eligibleRaces.Count > 0)
            {
                // No race specified — pick random eligible race
                PlayerRace playerRace = eligibleRaces[Util.Random(eligibleRaces.Count - 1)];
                Race = (short)playerRace.ID;
                Model = (ushort)playerRace.GetModel(gender);
                Gender = gender;
            }
            else
            {
                Race = owner?.Race ?? 1;
                Model = owner?.Model ?? 32;
                Gender = gender;
            }

            // Determine realm from class
            foreach (var kvp in GlobalConstants.STARTING_CLASSES_DICT)
            {
                if (kvp.Value.Contains((eCharacterClass)CharacterClass.ID))
                {
                    Realm = kvp.Key;
                    break;
                }
            }

            Size = (byte)Util.Random(45, 60);
        }

        #endregion

        #region Stats

        private void InitializeBotStats()
        {
            RebuildPlayerBaseStats();
        }

        private void ApplyLevelStatGrowth(int level)
        {
            // Level is authoritative, including multi-level promotion callers.
            // Never add historical growth to the values SetStats already rebuilt.
            RebuildPlayerBaseStats();
        }

        /// <summary>
        /// Migrates an already-running Bonedancer from the legacy mixed-ratio
        /// plan to the focused 50-point primary/secondary plan.  This changes
        /// only the future allocation policy; the current specialization
        /// levels and leftover points are deliberately left untouched.
        /// </summary>
        internal void EnsureBonedancerFocusedSpecPlan()
        {
            if (IsAutonomousWorldBot && !string.IsNullOrWhiteSpace(PersistentRecord?.SerializedBuildPlan))
                return; // A saved lifetime build is not a maintenance-time respec opportunity.
            if (CharacterClass?.ID != (int)eCharacterClass.Bonedancer || BotSpec == null)
                return;

            bool alreadyFocused = BotSpec.SpecLines.Count == 2 &&
                BotSpec.SpecLines.Any(line => line.levelRatio >= 1.0f && line.SpecCap >= 50) &&
                BotSpec.SpecLines.Any(line => line.levelRatio <= 0.0f && line.SpecCap >= 50);
            eSpecType primary = BotSpec.SpecType is eSpecType.DarkBone or eSpecType.SuppBone or eSpecType.ArmyBone
                ? BotSpec.SpecType
                : BotSpec.ChooseRandomSpecialization(eCharacterClass.Bonedancer);
            bool deterministic = IsAutonomousWorldBot;
            long seed = deterministic ? DatabaseID : ObjectID;
            if (!alreadyFocused)
                BotSpec = new BonedancerBotSpec(primary, seed, deterministic);

            string primaryName = primary switch
            {
                eSpecType.DarkBone => Specs.Darkness,
                eSpecType.ArmyBone => Specs.BoneArmy,
                _ => Specs.Suppression,
            };
            Specialization primarySpec = GetSpecializationByName(primaryName);
            Specialization[] boneSpecs = [
                GetSpecializationByName(Specs.Darkness),
                GetSpecializationByName(Specs.Suppression),
                GetSpecializationByName(Specs.BoneArmy)
            ];

            int expectedPrimary = Math.Min(50, (int)Level);
            bool legacySpread = boneSpecs.Count(spec => spec?.Level > 1) > 1;
            bool freshCompanionMissedFocusedTraining = IsTemporaryGroupHelper && primarySpec?.Level < expectedPrimary;
            bool legacyBotCannotLearnFirstMinion = IsAutonomousWorldBot && Level >= 15 &&
                primarySpec?.Level < 15 && boneSpecs.Any(spec => spec?.Level > 1);
            if (!legacySpread && !freshCompanionMissedFocusedTraining && !legacyBotCannotLearnFirstMinion)
                return;

            // Rebuild only a legacy/malformed Bonedancer allocation, using the
            // same normal point-spending routine as a fresh bot. No points are
            // invented: one randomly locked primary is bought to the legal 50
            // cap, and only points left at level 50 flow into its one locked
            // secondary. Other classes and real players never enter this path.
            foreach (Specialization spec in boneSpecs.Where(spec => spec != null))
                spec.Level = 1;
            m_leftOverSpecPoints = 0;
            SpendSpecPoints(Level, 0);
            RefreshSpecDependantSkills(false);
            GetAllUsableListSpells(true);
            SetBotSpells();
            SortSpells();
            TempProperties.RemoveProperty(AutonomousPetSupport.BonedancerSpellRefreshProperty);
            if (IsAutonomousWorldBot)
                MarkAutonomousStateDirty();
        }

        /// <summary>
        /// One-time migration for the legacy Savage plan which trained the
        /// health-cost buff line ahead of its weapon. Re-spend the same earned
        /// points through the normal trainer algorithm; no levels, points or
        /// item bonuses are invented.
        /// </summary>
        internal bool EnsureSavageWeaponSpecPlan()
        {
            if (CharacterClass?.ID != (int)eCharacterClass.Savage || BotSpec == null ||
                BotSpec.WeaponOneType == 0)
                return false;

            Specialization weapon = GetSpecializationByName(SkillBase.ObjectTypeToSpec(BotSpec.WeaponOneType));
            Specialization savagery = GetSpecializationByName(Specs.Savagery);
            if (weapon == null || savagery == null ||
                !SavageBotCombatPolicy.NeedsWeaponTrainingRepair(weapon.Level, savagery.Level))
                return false;

            foreach (Specialization spec in GetSpecList().Where(spec => spec.Trainable))
                spec.Level = 1;
            m_leftOverSpecPoints = 0;
            SpendSpecPoints(Level, 0);
            MarkAutonomousStateDirty();
            return true;
        }

        public void RebuildPlayerBaseStats()
        {
            if (!GlobalConstants.STARTING_STATS_DICT.TryGetValue((eRace)Race, out var racialStats))
                GlobalConstants.STARTING_STATS_DICT.TryGetValue(eRace.Unknown, out racialStats);

            ICharacterClass characterClass = CharacterClass;
            eStat primary = characterClass?.PrimaryStat ?? eStat.UNDEFINED;
            eStat secondary = characterClass?.SecondaryStat ?? eStat.UNDEFINED;
            eStat tertiary = characterClass?.TertiaryStat ?? eStat.UNDEFINED;
            bool advanced = characterClass?.HasAdvancedFromBaseClass() == true;
            int previousHealth = m_health;
            try
            {
                for (eStat stat = eStat._First; stat <= eStat._Last; stat++)
                {
                    int racialBase = racialStats != null && racialStats.TryGetValue(stat, out int value) ? value : 60;
                    int target = BotAttributeProgression.Calculate(racialBase, Level, stat, primary, secondary, tertiary, advanced);
                    int delta = target - GetBaseStat(stat);
                    if (delta != 0) ChangeBaseStat(stat, (short)delta);
                }
            }
            finally
            {
                // GameNPC.ChangeBaseStat(CON) invokes a setter that refills HP.
                // A stat rebuild must not heal; real level-up refills happen later.
                m_health = Math.Min(previousHealth, MaxHealth);
            }
            int maximumMana = MaxMana;
            if (Mana > maximumMana) Mana = maximumMana;
        }

        private void LoadPersistedSpecs(string serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized))
                return;
            foreach (string entry in serialized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = entry.Split('|', 2);
                if (parts.Length == 2 && int.TryParse(parts[1], out int level) && GetSpecializationByName(parts[0]) is Specialization spec)
                    spec.Level = Math.Clamp(level, 1, Level);
            }
        }

        private static byte ParseLastTrainedLevel(string serialized, byte currentLevel, string serializedSpecs)
        {
            const string prefix = "trained-level|";
            if (!string.IsNullOrWhiteSpace(serialized) && serialized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                byte.TryParse(serialized.Split('|')[1], out byte trained))
                return (byte)Math.Clamp((int)trained, 1, currentLevel);

            // Existing characters already have their trained spec levels saved.
            // Treat those as current so the migration never double-spends points.
            return string.IsNullOrWhiteSpace(serializedSpecs) ? (byte)1 : currentLevel;
        }

        /// <summary>"trained-level|N|realm-points|M": realm ability points already spent at a trainer (0 when absent).</summary>
        public static int ParseTrainedRealmPoints(string serialized)
        {
            string[] parts = serialized?.Split('|') ?? Array.Empty<string>();
            for (int i = 0; i + 1 < parts.Length; i++)
                if (parts[i].Equals("realm-points", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[i + 1], out int points))
                    return Math.Max(0, points);
            return 0;
        }

        public bool TrainPendingSpecializations(GameTrainer trainer)
        {
            if (!HasPendingAutonomousTraining && !AutonomousBotRealmAbilities.HasPurchase(this))
                return false;
            if (trainer == null ||
                trainer.ObjectState != eObjectState.Active || trainer.CurrentRegionID != CurrentRegionID ||
                !IsWithinRadius(trainer, 350) ||
                trainer.TrainedClass != eCharacterClass.Unknown && trainer.TrainedClass != (eCharacterClass)CharacterClass.ID)
                return false;

            // Realm abilities are bought at the same visit (the class trainer, as a player would).
            string realmAbilities = AutonomousBotRealmAbilities.Train(this);
            if (!HasPendingAutonomousTraining)
            {
                MarkAutonomousStateDirty();
                AutonomousBotStatusPersistence.Queue(this, true);
                return realmAbilities.Length > 0;
            }

            byte previous = _lastAutonomousTrainedLevel;
            Dictionary<string, int> beforeSpecs = GetSpecList()
                .Where(spec => spec.Trainable)
                .ToDictionary(spec => spec.KeyName, spec => spec.Level, StringComparer.OrdinalIgnoreCase);
            int abilitiesBefore = GetAllAbilities().Count;
            SpendSpecPoints(Level, previous);
            _lastAutonomousTrainedLevel = Level;
            RefreshSpecDependantSkills(false);
            SetBotSpells();
            SortStyles();
            SortSpells();
            BotRangedCombat.EnsureStarter(this); // Only an empty slot, once the weapon ability is learned.
            BotStarterInstruments.Ensure(this);
            // Advanced Albion weapon plans still need their ordinary base
            // weapon before the class grants the planned line.  Reconcile at
            // the exact unlock level without changing the bot's lifetime plan.
            eCharacterClass trainedClass = (eCharacterClass)CharacterClass.ID;
            if (trainedClass == eCharacterClass.Savage ||
                !ShouldUsePlannedTwoHandedPrimary(trainedClass, previous,
                    BotSpec?.Is2H == true, BotSpec?.WeaponTwoType ?? 0) &&
                ShouldUsePlannedTwoHandedPrimary(trainedClass, Level,
                    BotSpec?.Is2H == true, BotSpec?.WeaponTwoType ?? 0))
                EnsureAutonomousStarterWeapon();
            MarkAutonomousStateDirty();
            // Training and its real equipment/inventory result are durable via
            // the prioritized persistence queue. Do not stall an NPC service
            // thread behind another SQLite writer for several seconds.
            AutonomousBotStatusPersistence.Queue(this, true);

            string changes = string.Join(",", GetSpecList()
                .Where(spec => spec.Trainable && (!beforeSpecs.TryGetValue(spec.KeyName, out int oldLevel) || oldLevel != spec.Level))
                .Select(spec =>
                {
                    int oldLevel = beforeSpecs.TryGetValue(spec.KeyName, out int value) ? value : 0;
                    return $"{spec.KeyName}:{oldLevel}->{spec.Level}";
                }));
            if (AutonomousDiagnosticsProperties.Training)
                log.Info($"AUTONOMOUS_TRAINING bot=\"{Name}\" id={DatabaseID} class=\"{ClassName}\" level={Level} " +
                     $"trainer=\"{trainer.Name}\" trainedFrom={previous} trainedThrough={_lastAutonomousTrainedLevel} " +
                     $"specializations=\"{changes}\" abilities={abilitiesBefore}->{GetAllAbilities().Count} unspent={m_leftOverSpecPoints}");
            return true;
        }

        public override void CheckWeaponMagicalEffect(AttackData ad)
        {
            int charges = ad.Weapon?.PoisonCharges ?? 0;
            base.CheckWeaponMagicalEffect(ad);
            if (ad.Weapon != null && charges != ad.Weapon.PoisonCharges)
                BotPoisonSupply.Changed(this);
        }

        public void MarkAutonomousStateDirty()
        {
            if (IsAutonomousWorldBot)
                AutonomousStateDirty = true;
        }

        public bool SaveAutonomousState(bool includeInventory)
        {
            OfflineWorldBotRecord record = PrepareAutonomousStateSnapshot();
            if (record == null)
                return false;

            bool saved;
            lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
                saved = record.IsPersisted ? GameServer.Database.SaveObject(record) : GameServer.Database.AddObject(record);
            if (includeInventory && Inventory is BotInventory persistentInventory)
            {
                lock (AutonomousBotStatusPersistence.DatabaseWriteLock)
                    saved &= persistentInventory.SaveIntoDatabase(InternalID);
            }
            if (saved)
                AutonomousStateDirty = false;
            return saved;
        }

        public OfflineWorldBotRecord PrepareAutonomousStateSnapshot()
        {
            if (!IsAutonomousWorldBot || PersistentRecord == null)
                return null;

            OfflineWorldBotRecord record = PersistentRecord;
            record.Level = Level;
            record.Experience = Experience;
            record.RealmPoints = AutonomousRealmPoints;
            record.RegionId = CurrentRegionID;
            record.ZoneId = CurrentZone?.ID ?? 0;
            record.ZoneName = CurrentZone?.Description;
            record.X = X;
            record.Y = Y;
            record.Z = Z;
            record.Health = Math.Max(1, Health);
            record.Mana = Math.Max(0, Mana);
            record.Endurance = Math.Max(0, Endurance);
            record.IsAlive = IsAlive;
            record.IsOnline = ObjectState == eObjectState.Active;
            record.SerializedSpecs = string.Join(';', GetSpecList().Where(spec => spec.Trainable).Select(spec => $"{spec.KeyName}|{spec.Level}"));
            record.SerializedAbilities = $"trained-level|{_lastAutonomousTrainedLevel}|realm-points|{AutonomousBotRealmAbilities.TrainedPoints(this)}";
            record.UnspentSpecPoints = m_leftOverSpecPoints;
            record.LastSavedUtc = DateTime.UtcNow.ToString("O");
            record.LastUpdateUtc = record.LastSavedUtc;
            record.PersistedStateVersion = 1;
            record.Dirty = true;
            return record;
        }

        public void MarkAutonomousStateSaved() => AutonomousStateDirty = false;

        #endregion

        #region Specialization System

        protected virtual void AddSpecialization(Specialization skill, bool notify)
        {
            if (skill == null)
                return;

            lock (((ICollection)m_specialization).SyncRoot)
            {
                if (m_specialization.TryGetValue(skill.KeyName, out Specialization specialization))
                {
                    specialization.Level = skill.Level;
                    return;
                }

                m_specialization.Add(skill.KeyName, skill);
            }
        }

        public virtual bool RemoveSpecialization(string specKeyName)
        {
            lock (((ICollection)m_specialization).SyncRoot)
            {
                return m_specialization.Remove(specKeyName);
            }
        }

        public virtual IList<Specialization> GetSpecList()
        {
            lock (((ICollection)m_specialization).SyncRoot)
            {
                return m_specialization.Select(item => item.Value)
                    .OrderBy(it => it.LevelRequired).ThenBy(it => it.ID).ToList();
            }
        }

        public virtual Specialization GetSpecializationByName(string name)
        {
            lock (((ICollection)m_specialization).SyncRoot)
            {
                foreach (var entry in m_specialization)
                {
                    if (entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return entry.Value;
                }
            }

            return null;
        }

        public override int GetBaseSpecLevel(string keyName)
        {
            lock (((ICollection)m_specialization).SyncRoot)
            {
                if (m_specialization.TryGetValue(keyName, out Specialization spec))
                    return spec.Level;
            }

            return 0;
        }

        public override int GetModifiedSpecLevel(string keyName)
        {
            if (keyName.StartsWith(GlobalSpellsLines.Champion_Lines_StartWith))
                return 50;

            if (keyName.StartsWith(GlobalSpellsLines.Realm_Spells))
                return Level;

            Specialization spec = null;
            int level = 0;

            lock (((ICollection)m_specialization).SyncRoot)
            {
                if (!m_specialization.TryGetValue(keyName, out spec))
                {
                    if (keyName == GlobalSpellsLines.Combat_Styles_Effect)
                    {
                        if (CharacterClass.ID == (int)eCharacterClass.Reaver || CharacterClass.ID == (int)eCharacterClass.Heretic)
                            level = GetModifiedSpecLevel(Specs.Flexible);
                        if (CharacterClass.ID == (int)eCharacterClass.Valewalker)
                            level = GetModifiedSpecLevel(Specs.Scythe);
                        if (CharacterClass.ID == (int)eCharacterClass.Savage)
                            level = GetModifiedSpecLevel(Specs.Savagery);
                    }

                    return level;
                }
            }

            if (spec != null)
            {
                level = spec.Level;
                eProperty skillProp = SkillBase.SpecToSkill(keyName);
                if (skillProp != eProperty.Undefined)
                    level += GetModified(skillProp);
            }

            return level;
        }

        public virtual void LoadClassSpecializations(bool sendMessage)
        {
            if (CharacterClass == null)
                return;

            IDictionary<Specialization, int> careers = SkillBase.GetSpecializationCareer(CharacterClass.ID);
            var speclist = GetSpecList();
            var careerslist = careers.Keys.Select(k => k.KeyName.ToLower());

            foreach (var spec in speclist.Where(sp => sp.Trainable || !sp.AllowSave))
            {
                if (!careerslist.Contains(spec.KeyName.ToLower()))
                    RemoveSpecialization(spec.KeyName);
            }

            foreach (var constraint in careers)
            {
                if (constraint.Key is IMasterLevelsSpecialization)
                    continue;

                if (Level >= constraint.Value)
                {
                    if (!HasSpecialization(constraint.Key.KeyName))
                        AddSpecialization(constraint.Key, sendMessage);
                }
                else
                {
                    if (HasSpecialization(constraint.Key.KeyName))
                        RemoveSpecialization(constraint.Key.KeyName);
                }
            }
        }

        public void SpendSpecPoints(byte level, byte previousLevel)
        {
            if (BotSpec == null)
                return;

            BotSpec.SpecLines = BotSpec.SpecLines.OrderByDescending(ratio => ratio.levelRatio).ToList();

            int leftOverSpecPoints = m_leftOverSpecPoints;
            bool spentPoints = true;
            bool spendLeftOverPoints = false;
            bool botCreation = level - previousLevel > 1;

            byte index = (byte)(previousLevel + 1);

            if (level == previousLevel)
                index = level;

            for (byte i = index; i <= Level; i++)
            {
                spentPoints = true;
                spendLeftOverPoints = false;

                int totalSpecPointsThisLevel = GetSpecPointsForLevel(i, botCreation) + leftOverSpecPoints;

                while (spentPoints)
                {
                    spentPoints = false;

                    foreach (BotSpecLine specLine in BotSpec.SpecLines)
                    {
                        if (specLine.levelRatio <= 0 && Level < 50)
                            continue;

                        Specialization spec = GetSpecializationByName(specLine.Spec);

                        if (spec != null)
                        {
                            if (spec.Level < specLine.SpecCap && spec.Level < i)
                            {
                                int specRatio = (int)(i * specLine.levelRatio);

                                if (!spendLeftOverPoints && spec.Level >= specRatio)
                                    continue;

                                int totalCost = spec.Level + 1;

                                if (totalSpecPointsThisLevel >= totalCost)
                                {
                                    totalSpecPointsThisLevel -= totalCost;
                                    spec.Level++;
                                    spentPoints = true;
                                }
                            }
                        }
                    }

                    if (!spentPoints && !spendLeftOverPoints)
                    {
                        spendLeftOverPoints = true;
                        spentPoints = true;
                    }
                }

                m_leftOverSpecPoints = leftOverSpecPoints = totalSpecPointsThisLevel;
            }
        }

        private int GetSpecPointsForLevel(int level, bool botCreation)
        {
            int specpoints = 0;

            if (botCreation)
                if (level > 40)
                    specpoints += CharacterClass.SpecPointsMultiplier * (level - 1) / 20;

            if (level > 5)
                specpoints += CharacterClass.SpecPointsMultiplier * level / 10;
            else if (level >= 2)
                specpoints = level;

            return specpoints;
        }

        public virtual void RefreshSpecDependantSkills(bool sendMessages)
        {
            LoadClassSpecializations(sendMessages);

            lock (((ICollection)m_specialization).SyncRoot)
            {
                foreach (Specialization spec in m_specialization.Values)
                {
                    foreach (Ability ab in spec.GetAbilitiesForLiving(this))
                    {
                        if (!HasAbility(ab.KeyName) || GetAbility(ab.KeyName).Level < ab.Level)
                            AddAbility(ab, false);
                    }

                    foreach (Style st in spec.GetStylesForLiving(this))
                    {
                        AddStyle(st, false);
                    }

                    foreach (SpellLine sl in spec.GetSpellLinesForLiving(this))
                    {
                        AddSpellLine(sl, false);
                    }
                }
            }
        }

        public virtual void AddStyle(Style st, bool notify)
        {
            lock (Styles)
            {
                if (!Styles.Contains(st))
                    Styles.Add(st);
            }

            Styles = Styles;
        }

        public virtual void AddSpellLine(SpellLine line, bool notify)
        {
            if (line == null)
                return;

            SpellLine oldline = GetSpellLine(line.KeyName);
            if (oldline == null)
            {
                lock (m_spellLines)
                {
                    m_spellLines.Add(line);
                }
            }
            else
            {
                oldline.Level = line.Level;
            }
        }

        public virtual List<SpellLine> GetSpellLines()
        {
            lock (m_spellLines)
            {
                return new List<SpellLine>(m_spellLines);
            }
        }

        #endregion

        #region Spell Resolution

        protected ReaderWriterList<Tuple<Skill, Skill>> m_usableSkills = new ReaderWriterList<Tuple<Skill, Skill>>();
        protected ReaderWriterList<Tuple<SpellLine, List<Skill>>> m_usableListSpells = new ReaderWriterList<Tuple<SpellLine, List<Skill>>>();

        public virtual new List<Tuple<SpellLine, List<Skill>>> GetAllUsableListSpells(bool update = false)
        {
            List<Tuple<SpellLine, List<Skill>>> results = new List<Tuple<SpellLine, List<Skill>>>();

            if (!update)
            {
                if (m_usableListSpells.Count > 0)
                    results = new List<Tuple<SpellLine, List<Skill>>>(m_usableListSpells);

                if (results.Count > 0)
                    return results;
            }

            m_usableListSpells.FreezeWhile(innerList =>
            {
                List<Tuple<SpellLine, List<Skill>>> finalbase = new List<Tuple<SpellLine, List<Skill>>>();
                List<Tuple<SpellLine, List<Skill>>> finalspec = new List<Tuple<SpellLine, List<Skill>>>();

                foreach (Specialization spec in GetSpecList().Where(item => !item.HybridSpellList))
                {
                    var spells = spec.GetLinesSpellsForLiving(this);

                    foreach (SpellLine sl in spec.GetSpellLinesForLiving(this))
                    {
                        List<Tuple<SpellLine, List<Skill>>> working;
                        if (sl.IsBaseLine)
                            working = finalbase;
                        else
                            working = finalspec;

                        List<Skill> sps = new List<Skill>();
                        SpellLine key = spells.Keys.FirstOrDefault(el => el.ID == sl.ID);

                        if (key != null && spells.TryGetValue(key, out List<Skill> spellsInLine))
                        {
                            foreach (Skill sp in spellsInLine)
                                sps.Add(sp);
                        }

                        working.Add(new Tuple<SpellLine, List<Skill>>(sl, sps));
                    }
                }

                innerList.Clear();
                foreach (var tp in finalbase)
                {
                    innerList.Add(tp);
                    results.Add(tp);
                }

                foreach (var tp in finalspec)
                {
                    innerList.Add(tp);
                    results.Add(tp);
                }
            });

            return results;
        }

        public virtual List<Tuple<Skill, Skill>> GetAllUsableSkills(bool update = false)
        {
            List<Tuple<Skill, Skill>> results = new List<Tuple<Skill, Skill>>();

            if (!update)
            {
                if (m_usableSkills.Count > 0)
                    results = new List<Tuple<Skill, Skill>>(m_usableSkills);

                if (results.Count > 0)
                    return results;
            }

            m_usableSkills.FreezeWhile(innerList =>
            {
                IList<Specialization> specs = GetSpecList();
                List<Tuple<Skill, Skill>> copylist = new List<Tuple<Skill, Skill>>(innerList);

                foreach (Specialization spec in specs.Where(item => item.Trainable))
                {
                    int index = innerList.FindIndex(e => (e.Item1 is Specialization specialization) && specialization.ID == spec.ID);

                    if (index < 0)
                        innerList.Insert(innerList.Count(e => e.Item1 is Specialization), new Tuple<Skill, Skill>(spec, spec));
                    else
                    {
                        copylist.Remove(innerList[index]);
                        innerList[index] = new Tuple<Skill, Skill>(spec, spec);
                    }
                }

                foreach (Specialization spec in specs)
                {
                    foreach (Ability abv in spec.GetAbilitiesForLiving(this))
                    {
                        Ability ab = GetAbility(abv.KeyName);
                        if (ab == null)
                            ab = abv;

                        int index = innerList.FindIndex(k => (k.Item1 is Ability ability) && ability.ID == ab.ID);

                        if (index < 0)
                            innerList.Add(new Tuple<Skill, Skill>(ab, spec));
                        else
                        {
                            copylist.Remove(innerList[index]);
                            innerList[index] = new Tuple<Skill, Skill>(ab, spec);
                        }
                    }
                }

                foreach (Specialization spec in specs.Where(item => item.HybridSpellList))
                {
                    foreach (var sl in spec.GetLinesSpellsForLiving(this))
                    {
                        int index = -1;

                        foreach (Spell sp in sl.Value.Where(it => (it is Spell) && !((Spell)it).NeedInstrument).Cast<Spell>())
                        {
                            if (index < innerList.Count)
                                index = innerList.FindIndex(index + 1, e => (e.Item2 is SpellLine spellLine) && spellLine.ID == sl.Key.ID && (e.Item1 is Spell spell) && !spell.NeedInstrument);

                            if (index < 0 || index >= innerList.Count)
                            {
                                innerList.Add(new Tuple<Skill, Skill>(sp, sl.Key));
                                index = innerList.Count;
                            }
                            else
                            {
                                copylist.Remove(innerList[index]);
                                innerList[index] = new Tuple<Skill, Skill>(sp, sl.Key);
                            }
                        }
                    }
                }

                int songIndex = -1;
                foreach (Specialization spec in specs.Where(item => item.HybridSpellList))
                {
                    foreach (var sl in spec.GetLinesSpellsForLiving(this))
                    {
                        foreach (Spell sp in sl.Value.Where(it => (it is Spell) && ((Spell)it).NeedInstrument).Cast<Spell>())
                        {
                            if (songIndex < innerList.Count)
                                songIndex = innerList.FindIndex(songIndex + 1, e => (e.Item1 is Spell) && ((Spell)e.Item1).NeedInstrument);

                            if (songIndex < 0 || songIndex >= innerList.Count)
                            {
                                innerList.Add(new Tuple<Skill, Skill>(sp, sl.Key));
                                songIndex = innerList.Count;
                            }
                            else
                            {
                                copylist.Remove(innerList[songIndex]);
                                innerList[songIndex] = new Tuple<Skill, Skill>(sp, sl.Key);
                            }
                        }
                    }
                }

                foreach (Specialization spec in specs)
                {
                    foreach (Style st in spec.GetStylesForLiving(this))
                    {
                        int index = innerList.FindIndex(e => (e.Item1 is Style) && e.Item1.ID == st.ID);
                        if (index < 0)
                            innerList.Add(new Tuple<Skill, Skill>(st, spec));
                        else
                        {
                            copylist.Remove(innerList[index]);
                            innerList[index] = new Tuple<Skill, Skill>(st, spec);
                        }
                    }
                }

                foreach (var item in copylist)
                    innerList.Remove(item);

                foreach (var el in innerList)
                    results.Add(el);
            });

            return results;
        }

        private void SetBotSpells()
        {
            _powerSpellLines = new Dictionary<int, SpellLine>();
            if (CharacterClass == null)
                return;

            // Sluaghbinder is a hybrid combat class, but its three baseline
            // lines and the selected advanced line are ordinary list-caster
            // spell lines.  The generic hybrid path only reads
            // Specialization.HybridSpellList lines, so it silently produced a
            // spell-less bot (no Raise spell, pet upkeep, self buffs, or
            // instant Rot/Bane abilities).  Build its learned catalog from
            // both sources while retaining the originating line for power and
            // line-specific rank selection.
            if (CharacterClass.ID == (int)eCharacterClass.Sluaghbinder)
            {
                SetSluaghbinderSpells();
                return;
            }

            // Casters use list spells (highest level per type), hybrids use all usable skills
            if (CharacterClass.ClassType == eClassType.ListCaster)
                SetCasterSpells();
            else
                SetHybridSpells();

            if (IsEndgameCompanion)
                Spells = TemporaryCompanionBalance.HighestRanks(Spells, Level, SkillBase.GetSpellByID);
        }

        private void SetSluaghbinderSpells()
        {
            List<(Spell Spell, SpellLine Line)> learned = new();

            // Baseline and selected advanced lines are represented by the
            // normal list-spell resolver.  Refreshing here is intentional:
            // companions and persistent bots can be promoted/trained after
            // construction and must see the newly learned ranks immediately.
            foreach (Tuple<SpellLine, List<Skill>> tuple in GetAllUsableListSpells(true))
            {
                if (tuple?.Item1 == null || tuple.Item2 == null)
                    continue;

                // The Sluaghbinder bot build commits to one advanced path.
                // GetSpellLinesForLiving intentionally exposes class-hint
                // lines to NPCs, so explicitly keep only the baseline lines
                // plus the path recorded by SluaghbinderBotSpec.  Otherwise
                // an untrained Bulwark/Bane/Covenant line could leak its
                // abilities into every bot at the NPC 75%-level estimate.
                if (!IsSluaghbinderLineAllowed(tuple.Item1))
                    continue;

                foreach (Spell spell in tuple.Item2.OfType<Spell>())
                {
                    if (spell.Level <= Level && !SluaghbinderBotPolicy.IsPlayerOnlyServiceSpell(spell))
                        learned.Add((spell, tuple.Item1));
                }
            }

            // Keep this second pass for future class extensions that expose a
            // spell line through HybridSpellList.  It also prevents this
            // dedicated branch from regressing if a weapon/style-backed spell
            // is added to the class later.
            foreach (Tuple<Skill, Skill> tuple in GetAllUsableSkills(true))
            {
                if (tuple?.Item1 is not Spell spell || spell.Level > Level ||
                    SluaghbinderBotPolicy.IsPlayerOnlyServiceSpell(spell))
                    continue;

                SpellLine line = tuple.Item2 as SpellLine ?? ResolvePowerSpellLine(spell, null);
                if (!IsSluaghbinderLineAllowed(line))
                    continue;
                learned.Add((spell, line));
            }

            // Select one legal rank per effect role *within its spell line*.
            // The line is part of the key so Abhartach's Rot and the advanced
            // Abhartach's Bane DoT remain separate abilities and can stack as
            // intended.  The generic caster selector does not include that
            // line identity and would collapse them into one spell.
            List<(Spell Spell, SpellLine Line)> selected = learned
                .Where(entry => entry.Spell != null)
                .GroupBy(entry => new
                {
                    LineId = entry.Line?.ID ?? 0,
                    entry.Spell.DamageType,
                    entry.Spell.SpellType,
                    entry.Spell.Frequency,
                    entry.Spell.CastTime,
                    entry.Spell.Target,
                    entry.Spell.Group,
                    entry.Spell.IsPBAoE,
                    entry.Spell.Radius,
                    entry.Spell.SubSpellID
                })
                .Select(group => group
                    .OrderByDescending(entry => entry.Spell.Level)
                    .ThenByDescending(entry => entry.Spell.ID)
                    .First())
                .OrderBy(entry => entry.Line?.IsBaseLine == true ? 0 : 1)
                .ThenBy(entry => entry.Line?.ID ?? int.MaxValue)
                .ThenBy(entry => entry.Spell.Level)
                .ThenBy(entry => entry.Spell.ID)
                .ToList();

            Spells = selected.Select(entry => entry.Spell).ToList();
            foreach ((Spell spell, SpellLine line) in selected)
            {
                if (spell != null)
                    _powerSpellLines[spell.ID] = line;
            }
        }

        private bool IsSluaghbinderLineAllowed(SpellLine line)
        {
            if (line == null || line.IsBaseLine)
                return line != null;

            return BotSpec?.SpecLines?.Any(specLine =>
                string.Equals(specLine.Spec, line.KeyName, StringComparison.OrdinalIgnoreCase)) == true;
        }

        private void SetCasterSpells()
        {
            List<Spell> spells = new List<Spell>();

            var dict = GetAllUsableListSpells();

            if (dict != null && dict.Count > 0)
            {
                foreach (var tuple in dict)
                {
                    if (tuple.Item2.Count > 0)
                    {
                        foreach (Skill skill in tuple.Item2)
                        {
                            if (skill is Spell spell && spell.Level <= Level && !spells.Contains(spell))
                            {
                                spells.Add(spell);
                                _powerSpellLines.TryAdd(spell.ID, tuple.Item1);
                            }
                        }
                    }
                }
            }

            List<Spell> highestSpellLevels = IsEndgameCompanion
                ? TemporaryCompanionBalance.HighestRanks(spells, Level, SkillBase.GetSpellByID)
                : GetHighestLevelSpells(spells);

            if (highestSpellLevels.Count > 0)
                Spells = highestSpellLevels;
        }

        private void SetHybridSpells()
        {
            List<Spell> spells = new List<Spell>();

            var usableSkills = GetAllUsableSkills();

            for (int i = 0; i < usableSkills.Count; i++)
            {
                Skill skill = usableSkills[i].Item1;

                if (skill is Spell spell && spell.Level <= Level)
                    spells.Add(spell);
            }

            if (spells.Count > 0)
                Spells = spells;
        }

        public List<Spell> GetHighestLevelSpells(List<Spell> spells)
        {
            spells = spells.OrderByDescending(spell => spell.Level).ToList();

            List<Spell> highestLevelSpells = new List<Spell>();

            foreach (Spell currentSpell in spells)
            {
                Spell existingSpell = highestLevelSpells.FirstOrDefault(x => AreSpellsEqual(x, currentSpell));

                if (existingSpell != null)
                {
                    if (existingSpell.Level < currentSpell.Level)
                        highestLevelSpells.Remove(existingSpell);
                    else
                        continue;
                }

                highestLevelSpells.Add(currentSpell);
            }

            return highestLevelSpells;
        }

        private bool AreSpellsEqual(Spell spellOne, Spell spellTwo)
        {
            // Ranks of one proc line may differ in proc chance (Valewalker
            // Witherstrike 20 vs 15); they still replace one another
            // (stefanrows/OfflineDAoC fork, 0.185.0).
            bool sameProcLine = spellOne.SpellType is eSpellType.OffensiveProc or eSpellType.DefensiveProc &&
                                spellOne.Group != 0;
            return spellOne.DamageType == spellTwo.DamageType &&
                   spellOne.SpellType == spellTwo.SpellType &&
                   (sameProcLine || spellOne.Frequency == spellTwo.Frequency) &&
                   spellOne.CastTime == spellTwo.CastTime &&
                   spellOne.Target == spellTwo.Target &&
                   spellOne.Group == spellTwo.Group &&
                   spellOne.IsPBAoE == spellTwo.IsPBAoE;
        }

        #endregion

        #region Equipment

        public int BestArmorLevel
        {
            get
            {
                int bestLevel = -1;
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("AlbArmor"));
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("HibArmor"));
                bestLevel = Math.Max(bestLevel, GetAbilityLevel("MidArmor"));
                return bestLevel;
            }
        }

        public int BestShieldLevel => GetAbilityLevel("Shield");

        private void EquipBot(byte requestedEquipmentLevel = 0)
        {
            byte characterLevel = Level;
            byte equipmentLevel = (byte)TemporaryCompanionBalance.GearLevel(IsTemporaryGroupHelper, characterLevel, requestedEquipmentLevel);

            EquipmentLevelFloor = IsEndgameCompanion ? 50 : Math.Max(1, characterLevel - 10);
            EquipmentLevelCap = characterLevel;
            Inventory = new BotInventory();

            try
            {
                // Equipment generators read Level. Temporarily exposing the rolled
                // gear level keeps the character fully trained at its real level.
                Level = IsTemporaryGroupHelper ? characterLevel : equipmentLevel;
                // A Sluaghbinder Bane build, and a Covenant build that rolled
                // its scythe option, is a two-handed weapon plan.  The class
                // can use shields, but that does not make a shield part of
                // this build.  Do not let the generic Shield career grant it
                // an offhand on creation; doing so made every /spawn result
                // look like the default mace-and-shield loadout even when the
                // selected plan was supposed to use a scythe.
                bool sluaghbinderScythePlan = SluaghbinderUsesScythe;
                bool mayUseOffhand = !sluaghbinderScythePlan &&
                    (BotSpec?.SpecType is eSpecType.DualWield or eSpecType.DualWieldAndShield or eSpecType.LeftAxe ||
                     BestShieldLevel > 0);
                bool includeOffhand = TemporaryCompanionBalance.EquipOffhand(IsTemporaryGroupHelper, characterLevel, mayUseOffhand, Random.Shared.NextDouble());
                SetWeapons(includeOffhand);
                if (includeOffhand)
                    SetShield();
                SetRanged();
                BotRangedCombat.EnsureStarter(this);
                BotStarterInstruments.Ensure(this);
                if (IsTemporaryGroupHelper)
                    EnsureTemporaryHelperWeapon();
                Level = equipmentLevel;
                SetArmor();
                SetJewelry();
                _endgameCompanionEquipped = IsEndgameCompanion;
            }
            finally
            {
                Level = characterLevel;
                RefreshItemBonuses();
                if (!IsTemporaryGroupHelper)
                {
                    EquipmentLevelFloor = 0;
                    EquipmentLevelCap = 0;
                }
            }
        }

        // One-time promotion for helpers already present when their owner
        // reaches 50. Run on the bot's normal turn, never in a player event or
        // during combat/casting/horse travel. Preserve the existing build/group.
        internal bool TryApplyEndgameCompanionUpgrade()
        {
            if (!IsTemporaryGroupHelper || _endgameCompanionEquipped || Owner?.Level != 50 ||
                !IsAlive || IsCasting || InCombat || IsAttacking || IsOnStableMasterRoute ||
                Brain is BotBrain { HasAggro: true } || ControlledBrain?.Body is { InCombat: true })
                return false;

            byte previousLevel = Level;
            int healthPercent = HealthPercent, manaPercent = ManaPercent, endurancePercent = EndurancePercent;
            Level = 50;
            for (int level = Math.Max(6, previousLevel + 1); level <= Level; level++)
                ApplyLevelStatGrowth(level);
            if (previousLevel < Level)
                SpendSpecPoints(Level, previousLevel);
            RefreshSpecDependantSkills(false);
            GetAllUsableSkills(true);
            GetAllUsableListSpells(true);
            SetBotSpells();
            SortStyles();
            SortSpells();
            EquipBot(50);
            // Replace a pre-50 summon through its normal release lifecycle.
            // In particular this preserves Necromancer shade cleanup.
            AutonomousPetSupport.CancelPendingCharm(this);
            AutonomousPetSupport.ReleaseFieldTurrets(this);
            CommandNpcRelease();
            Health = Math.Max(1, MaxHealth * healthPercent / 100);
            Mana = MaxMana * manaPercent / 100;
            Endurance = MaxEndurance * endurancePercent / 100;
            UpdateNPCEquipmentAppearance();
            return true;
        }

        private void SetWeapons(bool includeOffhand = true)
        {
            if (BotSpec == null)
                return;

            switch (BotSpec.SpecType)
            {
                case eSpecType.SluaghbinderBulwark:
                case eSpecType.SluaghbinderBane:
                case eSpecType.SluaghbinderCovenant:
                    // The selected Sluaghbinder plan is part of the bot's
                    // identity.  Bane always starts with a usable scythe and
                    // Covenant may start with the scythe it rolled; they must
                    // not wait for a future loot drop before their build is
                    // reflected in the active weapon.  Bulwark (and a
                    // mace-and-shield Covenant) keeps the class's legal mace.
                    if (SluaghbinderUsesScythe)
                        BotEquipment.SetMeleeWeapon(this, eObjectType.Scythe, eHand.twoHand);
                    else
                        BotEquipment.SetMeleeWeapon(this, eObjectType.Blunt, eHand.oneHand);
                    break;

                case eSpecType.DualWield:
                case eSpecType.DualWieldAndShield:
                    BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.oneHand);
                    if (includeOffhand)
                        BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.leftHand);
                    break;

                case eSpecType.LeftAxe:
                    BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.oneHand);
                    BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.twoHand);
                    if (includeOffhand)
                        BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponTwoType, eHand.leftHand);
                    break;

                case eSpecType.OneHandAndShield:
                    BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.oneHand);
                    break;

                case eSpecType.OneHandHybrid:
                case eSpecType.TwoHandHybrid:
                case eSpecType.TwoHanded when CharacterClass.ID != (int)eCharacterClass.Valewalker:
                    BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.oneHand);
                    BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponTwoType, eHand.twoHand, BotSpec.DamageType);
                    break;

                case eSpecType.Mid:
                case eSpecType.PacHealer:
                case eSpecType.AugHealer:
                case eSpecType.MendHealer:
                case eSpecType.MendShaman:
                case eSpecType.AugShaman:
                case eSpecType.SubtShaman:
                    BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.oneHand);
                    BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.twoHand);
                    break;

                case eSpecType.Instrument:
                    BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.oneHand);
                    BotEquipment.SetInstrumentROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Instrument, eInventorySlot.TwoHandWeapon, eInstrumentType.Flute);
                    BotEquipment.SetInstrumentROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Instrument, eInventorySlot.DistanceWeapon, eInstrumentType.Drum);
                    BotEquipment.SetInstrumentROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Instrument, eInventorySlot.FirstEmptyBackpack, eInstrumentType.Lute);
                    break;

                default:
                    if (CharacterClass.ClassType == eClassType.ListCaster ||
                        CharacterClass.ID == (int)eCharacterClass.Friar ||
                        CharacterClass.ID == (int)eCharacterClass.Valewalker)
                        BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.twoHand);
                    else if (CharacterClass.ID != (int)eCharacterClass.Hunter)
                        BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.oneHand);
                    else
                        BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.twoHand);

                    if (BotSpec.WeaponOneType == eObjectType.Sword)
                        BotEquipment.SetMeleeWeapon(this, BotSpec.WeaponOneType, eHand.oneHand);
                    break;
            }

            if (CharacterClass.ID == (int)eCharacterClass.Savage &&
                SavageBotWeaponPolicy.TryBestGeneratedStarterSlot(this, out eActiveWeaponSlot savageSlot))
                SwitchWeapon(savageSlot);
            else if (PrimaryWeaponUsesTwoHands)
                SwitchWeapon(eActiveWeaponSlot.TwoHanded);
            else
                SwitchWeapon(eActiveWeaponSlot.Standard);
        }

        private static bool IsBackpackSlot(eInventorySlot slot) =>
            slot >= eInventorySlot.FirstBackpack && slot <= eInventorySlot.LastBackpack;

        public static int PlannedTwoHandedUnlockLevel(eCharacterClass classId,
            eObjectType plannedWeapon) => classId switch
        {
            // Classic Albion promotion gates. These bots begin directly in
            // their final class, so equipment must still respect the levels at
            // which a real character receives the advanced weapon ability.
            eCharacterClass.Paladin => 5,
            eCharacterClass.Armsman when plannedWeapon == eObjectType.PolearmWeapon => 5,
            eCharacterClass.Armsman when plannedWeapon == eObjectType.TwoHandedWeapon => 10,
            // Treat an unrecognized persisted Armsman 2H plan conservatively;
            // its base Slash/Crush/Thrust weapon is always legal in the interim.
            eCharacterClass.Armsman => 10,
            _ => 1,
        };

        public static bool ShouldUsePlannedTwoHandedPrimary(eCharacterClass classId,
            int level, bool plannedTwoHanded, eObjectType plannedWeapon = 0) =>
            plannedTwoHanded && level >= PlannedTwoHandedUnlockLevel(classId, plannedWeapon);

        private bool SluaghbinderUsesScythe =>
            CharacterClass?.ID == (int)eCharacterClass.Sluaghbinder &&
            BotSpec?.WeaponTwoType == eObjectType.Scythe &&
            BotSpec?.Is2H == true;

        private bool PrimaryWeaponUsesTwoHands =>
            (CharacterClass?.ID == (int)eCharacterClass.Sluaghbinder &&
             BotSpec?.WeaponTwoType == eObjectType.Scythe
                ? SluaghbinderUsesScythe
                : ShouldUsePlannedTwoHandedPrimary((eCharacterClass)(CharacterClass?.ID ?? 0),
                    Level, BotSpec?.Is2H == true, BotSpec?.WeaponTwoType ?? 0)) ||
            CharacterClass?.ClassType == eClassType.ListCaster ||
            CharacterClass?.ID is (int)eCharacterClass.Friar or (int)eCharacterClass.Valewalker;

        private eObjectType PreferredPrimaryWeaponType
        {
            get
            {
                if (BotSpec == null)
                    return 0;
                return BotWeaponStats.PrimaryType(BotSpec.WeaponOneType, BotSpec.WeaponTwoType, PrimaryWeaponUsesTwoHands);
            }
        }

        private static int OwnedWeaponScore(DbInventoryItem item, eObjectType preferredType)
        {
            if (item == null)
                return int.MinValue;
            int preferred = (eObjectType)item.Object_Type == preferredType ? 1_000_000 : 0;
            return preferred + Math.Max(0, item.Level) * 10_000 + Math.Max(0, item.DPS_AF) * 100 +
                   Math.Max(0, item.Quality) + Math.Max(0, item.Bonus) * 2;
        }

        private bool IsOwnedWeaponCandidate(DbInventoryItem item, eInventorySlot target, bool twoHandOnly)
        {
            if (!BotWeaponStats.FitsConfiguredSlot(this, item, target))
                return false;

            // Item_Type is retained when loot is placed in the backpack. It is
            // the authoritative hand shape; SlotPosition is only where that
            // particular item currently lives.
            bool twoHand = item.Item_Type == Slot.TWOHAND || item.SlotPosition == (int)eInventorySlot.TwoHandWeapon;
            if (twoHandOnly)
                return twoHand;
            return !twoHand || item.SlotPosition == (int)target;
        }

        private bool TryEquipOwnedWeaponUpgrade(eInventorySlot target, eObjectType preferredType, bool twoHandOnly)
        {
            if (Inventory == null)
                return false;

            DbInventoryItem current = Inventory.GetItem(target);
            int currentScore = BotWeaponStats.FitsConfiguredSlot(this, current, target)
                ? OwnedWeaponScore(current, preferredType) : int.MinValue;
            DbInventoryItem candidate = Inventory.AllItems
                .Where(item => IsOwnedWeaponCandidate(item, target, twoHandOnly) &&
                               ((eInventorySlot)item.SlotPosition == target ||
                                (!twoHandOnly && target == eInventorySlot.RightHandWeapon &&
                                 (eInventorySlot)item.SlotPosition == eInventorySlot.LeftHandWeapon) ||
                                IsBackpackSlot((eInventorySlot)item.SlotPosition)))
                .OrderByDescending(item => OwnedWeaponScore(item, preferredType))
                .FirstOrDefault();
            if (candidate == null || OwnedWeaponScore(candidate, preferredType) <= currentScore)
                return false;

            eInventorySlot source = (eInventorySlot)candidate.SlotPosition;
            if (source != target && !Inventory.MoveItem(source, target, Math.Max(1, candidate.Count)))
                return false;

            RefreshItemBonuses();
            SwitchWeapon(target == eInventorySlot.TwoHandWeapon
                ? eActiveWeaponSlot.TwoHanded : eActiveWeaponSlot.Standard);
            return true;
        }

        private bool TryMoveEquippedItemToBackpack(eInventorySlot slot)
        {
            DbInventoryItem item = Inventory?.GetItem(slot);
            if (item == null)
                return true;
            eInventorySlot storage = Inventory.FindFirstEmptySlot(
                eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
            if (storage == eInventorySlot.Invalid)
                storage = Inventory.FindFirstEmptySlot(eInventorySlot.FirstVault, eInventorySlot.LastVault);
            if (storage != eInventorySlot.Invalid &&
                Inventory.MoveItem(slot, storage, Math.Max(1, item.Count)))
                return true;

            // Explicit cleanup policy for legacy bad loadouts: a focus staff
            // must never remain equipped by a melee class merely because all
            // forty backpack slots are occupied. Friars are the sole staff
            // melee class; caster plans legitimately keep their focus staff.
            bool invalidMeleeStaff = (eObjectType)item.Object_Type == eObjectType.Staff &&
                                     CharacterClass?.ID != (int)eCharacterClass.Friar &&
                                     CharacterClass?.ClassType != eClassType.ListCaster;
            return invalidMeleeStaff && Inventory.RemoveItem(item);
        }

        private bool RequiresMeleeOffhand => BotSpec?.SpecType is
            eSpecType.DualWield or eSpecType.DualWieldAndShield or eSpecType.LeftAxe;

        private eObjectType PreferredOffhandWeaponType =>
            BotSpec?.SpecType == eSpecType.LeftAxe && BotSpec.WeaponTwoType != 0
                ? BotSpec.WeaponTwoType
                : BotSpec?.WeaponOneType ?? (eObjectType)0;

        private bool EnsureConfiguredMeleeOffhand()
        {
            if (!RequiresMeleeOffhand || PreferredOffhandWeaponType == 0)
                return false;

            DbInventoryItem current = Inventory.GetItem(eInventorySlot.LeftHandWeapon);
            if (BotWeaponStats.FitsConfiguredSlot(this, current, eInventorySlot.LeftHandWeapon))
                return false;

            // A bag/armor/focus item in the visible offhand is inventory, not
            // a weapon. Preserve it in the backpack before selecting or
            // creating the class weapon that the build actually trained.
            if (current != null && !TryMoveEquippedItemToBackpack(eInventorySlot.LeftHandWeapon))
                return false;

            if (TryEquipOwnedWeaponUpgrade(eInventorySlot.LeftHandWeapon,
                    PreferredOffhandWeaponType, false))
                return true;

            return TryCreateConfiguredStarterWeapon(
                eInventorySlot.LeftHandWeapon, PreferredOffhandWeaponType);
        }

        private bool StowInvalidInactiveWeaponSlots(eInventorySlot primarySlot)
        {
            bool changed = false;
            foreach (eInventorySlot slot in new[]
                     {
                         eInventorySlot.RightHandWeapon,
                         eInventorySlot.TwoHandWeapon,
                     })
            {
                if (slot == primarySlot)
                    continue;
                DbInventoryItem item = Inventory.GetItem(slot);
                if (item != null && !BotWeaponStats.CanUseMelee(this, item) &&
                    TryMoveEquippedItemToBackpack(slot))
                    changed = true;
            }
            return changed;
        }

        private bool TryCreateConfiguredStarterWeapon(eInventorySlot target, eObjectType type)
        {
            if (Inventory == null || type == 0 || !BotWeaponStats.IsMeleeWeapon(type))
                return false;

            DbInventoryItem occupied = Inventory.GetItem(target);
            if (BotWeaponStats.FitsConfiguredSlot(this, occupied, target))
            {
                SwitchWeapon(target == eInventorySlot.TwoHandWeapon
                    ? eActiveWeaponSlot.TwoHanded : eActiveWeaponSlot.Standard);
                return false;
            }

            eInventorySlot displacedSlot = eInventorySlot.Invalid;
            if (occupied != null)
            {
                // Builds prior to the persistent-fallback fix could save the
                // inventory relation but not its generated unique template.
                // Remove only that unusable GameBot-created row so a full bag
                // cannot permanently block the correct class weapon repair.
                if (BotWeaponStats.IsBrokenGeneratedFallback(occupied) && Inventory.RemoveItem(occupied))
                    occupied = null;
            }
            if (occupied != null)
            {
                displacedSlot = Inventory.FindFirstEmptySlot(eInventorySlot.FirstBackpack, eInventorySlot.LastBackpack);
                if (displacedSlot == eInventorySlot.Invalid)
                    displacedSlot = Inventory.FindFirstEmptySlot(eInventorySlot.FirstVault, eInventorySlot.LastVault);
                if (displacedSlot == eInventorySlot.Invalid)
                {
                    bool invalidMeleeStaff = (eObjectType)occupied.Object_Type == eObjectType.Staff &&
                                             CharacterClass.ID != (int)eCharacterClass.Friar &&
                                             CharacterClass.ClassType != eClassType.ListCaster;
                    if (!invalidMeleeStaff || !Inventory.RemoveItem(occupied))
                        return false;
                    occupied = null;
                }
                else if (!Inventory.MoveItem(target, displacedSlot, Math.Max(1, occupied.Count)))
                    return false;
            }

            var generated = new GeneratedUniqueItem(false, Realm, (eCharacterClass)CharacterClass.ID,
                (byte)Math.Clamp((int)Level, 1, (int)MaxLevel), type, target);
            generated.Item_Type = (int)target;
            generated.DPS_AF = Math.Max(generated.DPS_AF, BotWeaponStats.NormalDps(Level));
            generated.Quality = Math.Max(generated.Quality, 85);
            generated.SPD_ABS = Math.Max(generated.SPD_ABS, 20);
            generated.MaxCondition = Math.Max(generated.MaxCondition, 50_000);
            generated.Condition = Math.Max(generated.Condition, generated.MaxCondition);
            generated.MaxDurability = Math.Max(generated.MaxDurability, generated.MaxCondition);
            generated.Durability = Math.Max(generated.Durability, generated.MaxDurability);
            // GeneratedUniqueItem intentionally defaults to AllowAdd=false.
            // Persistent GameBot inventories need the unique side of the
            // relation saved with the inventory row or the weapon reloads as a
            // blank, unusable template after the next server start.
            if (Inventory is BotInventory { IsPersistent: true })
                generated.AllowAdd = true;

            GameInventoryItem replacement = GameInventoryItem.Create(generated);
            replacement.Creator = nameof(GameBot);
            replacement.Realm = (int)Realm;
            if (!Inventory.AddItem(target, replacement) || !BotWeaponStats.CanUseMelee(this, replacement))
            {
                if (Inventory.GetItem(target) == replacement)
                    Inventory.RemoveItemWithoutDbDeletion(replacement);
                if (occupied != null && displacedSlot != eInventorySlot.Invalid && Inventory.GetItem(target) == null)
                    Inventory.MoveItem(displacedSlot, target, Math.Max(1, occupied.Count));
                return false;
            }

            RefreshItemBonuses();
            SwitchWeapon(target == eInventorySlot.TwoHandWeapon
                ? eActiveWeaponSlot.TwoHanded : eActiveWeaponSlot.Standard);
            return true;
        }

        /// <summary>
        /// GameBot-only weapon reconciliation. Existing real inventory is
        /// preferred over creating anything: the highest-level class-valid
        /// weapon in the bot's equipment/backpack is moved into the primary
        /// hand as an upgrade. Invalid occupied slots are displaced into the
        /// backpack before a normalized fallback is created, so no item is
        /// silently deleted and no staff can become a hunter/shadowblade's
        /// melee weapon.
        /// </summary>
        private bool EnsureAutonomousStarterWeapon()
        {
            if (Inventory == null || CharacterClass == null || BotSpec == null)
                return false;

            if (CharacterClass.ID == (int)eCharacterClass.Savage)
                return EnsureSavageWeaponReady();

            eInventorySlot target = PrimaryWeaponUsesTwoHands
                ? eInventorySlot.TwoHandWeapon : eInventorySlot.RightHandWeapon;
            eObjectType preferredType = PreferredPrimaryWeaponType;
            if (preferredType == 0)
                return false;

            bool changed = TryEquipOwnedWeaponUpgrade(target, preferredType, PrimaryWeaponUsesTwoHands);

            DbInventoryItem current = Inventory.GetItem(target);
            if (!BotWeaponStats.FitsConfiguredSlot(this, current, target))
            {
                changed |= TryCreateConfiguredStarterWeapon(target, preferredType);
                current = Inventory.GetItem(target);
            }

            changed |= EnsureConfiguredMeleeOffhand();
            bool hasSluaghbinderShield = Inventory.AllItems.Any(item =>
                item != null && (eObjectType)item.Object_Type == eObjectType.Shield);
            if (CharacterClass.ID == (int)eCharacterClass.Sluaghbinder &&
                !SluaghbinderUsesScythe && BestShieldLevel > 0 && !hasSluaghbinderShield)
            {
                // Bulwark and mace-and-shield Covenant use the class-career
                // shield.  A scythe plan intentionally has no active offhand.
                BotEquipment.SetShield(this, BestShieldLevel);
                changed = true;
            }
            changed |= StowInvalidInactiveWeaponSlots(target);

            bool primaryReady = BotWeaponStats.FitsConfiguredSlot(this, Inventory.GetItem(target), target);
            if (primaryReady)
                SwitchWeapon(target == eInventorySlot.TwoHandWeapon
                    ? eActiveWeaponSlot.TwoHanded : eActiveWeaponSlot.Standard);
            else
            {
                // A full backpack can make it impossible to stow an old item
                // immediately. Never display/use it: fall back to any legal
                // standard or two-hand weapon already owned, and retry after
                // the next inventory-maintenance vendor pass creates space.
                bool standardReady = BotWeaponStats.CanUseMelee(this,
                    Inventory.GetItem(eInventorySlot.RightHandWeapon));
                bool twoHandReady = BotWeaponStats.CanUseMelee(this,
                    Inventory.GetItem(eInventorySlot.TwoHandWeapon));
                if (standardReady)
                    SwitchWeapon(eActiveWeaponSlot.Standard);
                else if (twoHandReady)
                    SwitchWeapon(eActiveWeaponSlot.TwoHanded);
                else if (log.IsWarnEnabled && GameLoop.GameLoopTime >= _nextWeaponRepairWarningTick)
                {
                    // Weapon reconciliation runs from the ordinary bot pulse.
                    // A full backpack or a temporarily unavailable service is
                    // not a new failure every pulse; rate-limit the diagnostic
                    // while retaining the retry and preserving all real items.
                    _nextWeaponRepairWarningTick = GameLoop.GameLoopTime + 60_000;
                    log.Warn($"No usable {preferredType} weapon or free replacement slot for bot {Name}; preserving existing inventory.");
                }
            }

            return changed;
        }

        private bool TryEquipBestSavageWeapon(eInventorySlot target)
        {
            DbInventoryItem current = Inventory.GetItem(target);
            double currentScore = SavageBotWeaponPolicy.CombatScore(this, current, target);
            DbInventoryItem best = Inventory.AllItems
                .Where(item => item != null &&
                    ((eInventorySlot)item.SlotPosition == target ||
                     IsBackpackSlot((eInventorySlot)item.SlotPosition) ||
                     target == eInventorySlot.RightHandWeapon &&
                     (eInventorySlot)item.SlotPosition == eInventorySlot.LeftHandWeapon))
                .OrderByDescending(item => SavageBotWeaponPolicy.CombatScore(this, item, target))
                .FirstOrDefault();
            if (best == null || SavageBotWeaponPolicy.CombatScore(this, best, target) <= currentScore)
                return false;

            eInventorySlot source = (eInventorySlot)best.SlotPosition;
            if (!Inventory.MoveItem(source, target, Math.Max(1, best.Count)))
                return false;
            RefreshItemBonuses();
            return true;
        }

        private bool EnsureSavageWeaponReady()
        {
            bool changed = TryEquipBestSavageWeapon(eInventorySlot.RightHandWeapon);
            bool handToHand = BotSpec.WeaponOneType == eObjectType.HandToHand;
            changed |= TryEquipBestSavageWeapon(handToHand
                ? eInventorySlot.LeftHandWeapon : eInventorySlot.TwoHandWeapon);

            if (!SavageBotWeaponPolicy.TryBestActiveSlot(this, out eActiveWeaponSlot activeSlot))
            {
                eInventorySlot starterSlot = handToHand
                    ? eInventorySlot.RightHandWeapon : eInventorySlot.TwoHandWeapon;
                changed |= TryCreateConfiguredStarterWeapon(starterSlot, BotSpec.WeaponOneType);
            }
            if (handToHand && SavageBotWeaponPolicy.CombatScore(this,
                    Inventory.GetItem(eInventorySlot.LeftHandWeapon), eInventorySlot.LeftHandWeapon) <= 0)
            {
                DbInventoryItem offhand = Inventory.GetItem(eInventorySlot.LeftHandWeapon);
                if (offhand == null || TryMoveEquippedItemToBackpack(eInventorySlot.LeftHandWeapon))
                    changed |= TryCreateConfiguredStarterWeapon(eInventorySlot.LeftHandWeapon, BotSpec.WeaponOneType);
            }
            else if (!handToHand && Inventory.GetItem(eInventorySlot.LeftHandWeapon) != null &&
                     TryMoveEquippedItemToBackpack(eInventorySlot.LeftHandWeapon))
                changed = true;

            // Legacy slots may still hold unusable weapons. Preserve the items
            // in storage when possible, while the active choice stays legal.
            changed |= StowInvalidInactiveWeaponSlots(eInventorySlot.Invalid);
            if (SavageBotWeaponPolicy.TryBestActiveSlot(this, out activeSlot))
                SwitchWeapon(activeSlot);
            return changed;
        }

        // Called by BotBrain only after a melee decision found no legal active
        // weapon.  Keeping the reconciliation behind this GameBot boundary
        // prevents ordinary GameNPC and real-player equipment from changing.
        internal bool EnsureBotWeaponReady()
        {
            bool changed = EnsureAutonomousStarterWeapon();
            if (changed && IsAutonomousWorldBot)
            {
                MarkAutonomousStateDirty();
                AutonomousBotEconomy.MarkInventoryChanged(this);
                AutonomousBotStatusPersistence.Queue(this, true);
            }
            return changed;
        }

        private void EnsureTemporaryHelperWeapon()
        {
            EnsureAutonomousStarterWeapon();
            bool hasUsableWeapon = BotWeaponStats.CanUseMelee(this, Inventory.GetItem(eInventorySlot.RightHandWeapon)) ||
                                   BotWeaponStats.CanUseMelee(this, Inventory.GetItem(eInventorySlot.TwoHandWeapon)) ||
                                   BotRangedCombat.CanUse(this, Inventory.GetItem(eInventorySlot.DistanceWeapon));
            if (hasUsableWeapon || BotSpec == null)
                return;

            bool useTwoHands = PrimaryWeaponUsesTwoHands;
            eInventorySlot slot = useTwoHands ? eInventorySlot.TwoHandWeapon : eInventorySlot.RightHandWeapon;
            BotEquipment.SetWeaponROG(
                this,
                Realm,
                (eCharacterClass)CharacterClass.ID,
                Level,
                BotSpec.WeaponOneType,
                slot,
                eDamageType.Slash);
            SwitchWeapon(useTwoHands ? eActiveWeaponSlot.TwoHanded : eActiveWeaponSlot.Standard);
        }

        private void SetShield()
        {
            BotEquipment.SetShield(this, BestShieldLevel);
        }

        private void SetRanged()
        {
            foreach (Ability ability in GetAllAbilities())
            {
                switch (ability.KeyName)
                {
                    case "Weaponry: Thrown": BotEquipment.SetRangedWeapon(this, eObjectType.Thrown); break;
                    case "Weaponry: Shortbows": BotEquipment.SetRangedWeapon(this, eObjectType.Fired); break;
                    case "Weaponry: Crossbow": BotEquipment.SetRangedWeapon(this, eObjectType.Crossbow); break;
                    case "Weaponry: Recurved Bows": BotEquipment.SetRangedWeapon(this, eObjectType.RecurvedBow); SwitchWeapon(eActiveWeaponSlot.Distance); break;
                    case "Weaponry: Longbows": BotEquipment.SetRangedWeapon(this, eObjectType.Longbow); SwitchWeapon(eActiveWeaponSlot.Distance); break;
                    case "Weaponry: Composite Bows": BotEquipment.SetRangedWeapon(this, eObjectType.CompositeBow); SwitchWeapon(eActiveWeaponSlot.Distance); break;
                }
            }
        }

        private void SetArmor()
        {
            int armorLevel = BestArmorLevel;
            eObjectType armorType = eObjectType.GenericArmor;

            switch (armorLevel)
            {
                case 1: armorType = eObjectType.Cloth; break;
                case 2: armorType = eObjectType.Leather; break;
                case 3:
                    armorType = (Realm == eRealm.Hibernia) ? eObjectType.Reinforced : eObjectType.Studded;
                    break;
                case 4:
                    armorType = (Realm == eRealm.Hibernia) ? eObjectType.Scale : eObjectType.Chain;
                    break;
                case 5: armorType = eObjectType.Plate; break;
            }

            BotEquipment.SetArmor(this, armorType);
        }

        private void SetJewelry()
        {
            BotEquipment.SetJewelryROG(this, Realm, (eCharacterClass)CharacterClass.ID, Level, eObjectType.Magical);
        }

        public virtual void RefreshItemBonuses()
        {
            ItemBonus.Clear();
            string slotToLoad = string.Empty;

            switch (VisibleActiveWeaponSlots)
            {
                case 16: slotToLoad = "rightandleftHandSlot"; break;
                case 18: slotToLoad = "leftandtwoHandSlot"; break;
                case 31: slotToLoad = "leftHandSlot"; break;
                case 34: slotToLoad = "twoHandSlot"; break;
                case 51: slotToLoad = "distanceSlot"; break;
                case 240: slotToLoad = "righttHandSlot"; break;
                case 242: slotToLoad = "twoHandSlot"; break;
            }

            foreach (DbInventoryItem item in Inventory.EquippedItems)
            {
                if (item == null)
                    continue;

                bool add = true;

                if (slotToLoad != string.Empty)
                {
                    switch (item.SlotPosition)
                    {
                        case Slot.TWOHAND:
                            if (!slotToLoad.Contains("twoHandSlot"))
                                add = false;
                            break;
                        case Slot.RIGHTHAND:
                            if (!slotToLoad.Contains("right"))
                                add = false;
                            break;
                        case Slot.SHIELD:
                        case Slot.LEFTHAND:
                            if (!slotToLoad.Contains("left"))
                                add = false;
                            break;
                        case Slot.RANGED:
                            if (slotToLoad != "distanceSlot")
                                add = false;
                            break;
                    }
                }

                if (!add)
                    continue;

                if (item.IsMagical)
                {
                    if (item.Bonus1 != 0) ItemBonus[(eProperty)item.Bonus1Type] += item.Bonus1;
                    if (item.Bonus2 != 0) ItemBonus[(eProperty)item.Bonus2Type] += item.Bonus2;
                    if (item.Bonus3 != 0) ItemBonus[(eProperty)item.Bonus3Type] += item.Bonus3;
                    if (item.Bonus4 != 0) ItemBonus[(eProperty)item.Bonus4Type] += item.Bonus4;
                    if (item.Bonus5 != 0) ItemBonus[(eProperty)item.Bonus5Type] += item.Bonus5;
                    if (item.Bonus6 != 0) ItemBonus[(eProperty)item.Bonus6Type] += item.Bonus6;
                    if (item.Bonus7 != 0) ItemBonus[(eProperty)item.Bonus7Type] += item.Bonus7;
                    if (item.Bonus8 != 0) ItemBonus[(eProperty)item.Bonus8Type] += item.Bonus8;
                    if (item.Bonus9 != 0) ItemBonus[(eProperty)item.Bonus9Type] += item.Bonus9;
                    if (item.Bonus10 != 0) ItemBonus[(eProperty)item.Bonus10Type] += item.Bonus10;
                    if (item.ExtraBonus != 0) ItemBonus[(eProperty)item.ExtraBonusType] += item.ExtraBonus;
                }
            }
        }

        public override DbInventoryItem ActiveLeftWeapon =>
            CharacterClass?.ID == (int)eCharacterClass.Savage &&
            (BotSpec?.WeaponOneType != eObjectType.HandToHand ||
             !BotWeaponStats.FitsConfiguredSlot(this, base.ActiveLeftWeapon, eInventorySlot.LeftHandWeapon))
                ? null : base.ActiveLeftWeapon;

        public override void SwitchWeapon(eActiveWeaponSlot slot)
        {
            // GameNPC.SwitchWeapon intentionally stops and restarts an active
            // attack, even when the requested slot is already selected. That
            // is useful for scripted NPCs, but inventory/equipment refreshes
            // can legitimately repeat the current slot for a GameBot. During
            // a bow draw that reset RangedAttackState to None and produced an
            // endless prepare animation instead of an arrow release.
            //
            // Only suppress a true no-op. If an item was replaced in the same
            // slot (or the standard offhand changed), run the native switch so
            // active item references and appearance are still refreshed.
            if (slot == ActiveWeaponSlot && ActiveSlotMatchesInventory(slot))
            {
                if (Inventory != null)
                    RefreshItemBonuses();
                return;
            }

            base.SwitchWeapon(slot);
            if (Inventory != null)
                RefreshItemBonuses();
        }

        private bool ActiveSlotMatchesInventory(eActiveWeaponSlot slot)
        {
            if (Inventory == null)
                return false;

            return slot switch
            {
                eActiveWeaponSlot.Standard =>
                    ReferenceEquals(ActiveWeapon, Inventory.GetItem(eInventorySlot.RightHandWeapon)) &&
                    ReferenceEquals(ActiveLeftWeapon,
                        CharacterClass?.ID == (int)eCharacterClass.Savage &&
                        (BotSpec?.WeaponOneType != eObjectType.HandToHand ||
                         !BotWeaponStats.FitsConfiguredSlot(this,
                             Inventory.GetItem(eInventorySlot.LeftHandWeapon), eInventorySlot.LeftHandWeapon))
                            ? null : Inventory.GetItem(eInventorySlot.LeftHandWeapon)),
                eActiveWeaponSlot.TwoHanded =>
                    ReferenceEquals(ActiveWeapon, Inventory.GetItem(eInventorySlot.TwoHandWeapon)),
                eActiveWeaponSlot.Distance =>
                    ReferenceEquals(ActiveWeapon, Inventory.GetItem(eInventorySlot.DistanceWeapon)),
                _ => false,
            };
        }

        #endregion

        #region Database

        public void SaveToDatabase()
        {
            if (IsTemporaryGroupHelper)
                return;
            BotDatabase.SaveBot(this);
        }

        public void LoadFromDatabase()
        {
            log.Debug($"LoadFromDatabase called for bot {Name}");
        }

        #endregion

        #region Movement

        /// <summary>
        /// Synchronizes the NPC movement component with coordinates assigned
        /// before AddToWorld. Login placement and validation run before the
        /// normal AddToWorld synchronization, so they must explicitly perform
        /// this step after loading or choosing a saved starting position.
        /// </summary>
        internal void SynchronizePositionForLoginValidation()
        {
            movementComponent.ForceUpdatePosition();
        }

        public override void WalkTo(Vector3 position, short speed)
        {
            if (IsOnStableMasterRoute)
                return;

            if (speed > 0 && IsRecoveryResting)
                return;

            // Persistent actors use collision-safe navigation for every ordinary
            // walk order, including combat approaches and service interactions.
            // A tiny cross-zone seam step remains direct because Detour meshes
            // are stored per zone and cannot calculate across that boundary.
            Zone destinationZone = CurrentRegion?.GetZone((int)position.X, (int)position.Y);
            if (IsAutonomousWorldBot && CurrentZone != null && destinationZone == CurrentZone)
                base.PathTo(position, speed);
            else
                base.WalkTo(position, speed);
        }

        public override void PathTo(Vector3 position, short speed)
        {
            if (speed > 0 && IsRecoveryResting)
                return;
            base.PathTo(position, speed);
        }

        public override void Follow(GameObject target, int minDistance, int maxDistance)
        {
            if (target != null && IsRecoveryResting)
                return;
            base.Follow(target, minDistance, maxDistance);
        }

        public new void StopFollowing()
        {
            base.StopFollowing();
        }

        public void MoveTo(Vector3 position)
        {
            WalkTo(position, MaxSpeed);
        }

        public override bool IsMoving => base.IsMoving || IsStrafing;

        #endregion

        #region Effectiveness

        public override double Effectiveness
        {
            get => 1.0;
            set { }
        }

        double IGamePlayer.Effectiveness
        {
            get => Effectiveness;
            set { }
        }

        #endregion

        #region InteractDistance

        public override int InteractDistance => WorldMgr.VISIBILITY_DISTANCE;

        #endregion
    }
}
