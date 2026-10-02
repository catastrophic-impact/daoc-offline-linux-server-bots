using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Logging;

namespace DOL.GS
{
    /// <summary>
    /// Shroom decisions for Animist gamebots and /spawn companions (both are
    /// GameBots and share this path). Native summons, turret brains, spell
    /// costs and the server's turret caps remain authoritative; the order of
    /// play lives in <see cref="AnimistShroomPlanner"/>.
    /// </summary>
    public static class BotAnimistPolicy
    {
        private sealed class State
        {
            public long NextRelocation;
            public long NextMainSwap;
            public long NextDiagnostic;
            public GameLiving PendingTarget;
            public GameObject PendingCastTarget;
            public bool PendingRestore;
        }

        /// <summary>The best learned rank of every Animist tool this bot can cast right now.</summary>
        private sealed class Kit
        {
            public (Spell Spell, SpellLine Line) DamageMain, TauntMain, DamageField, Ablative, Spore, Burst, HealMain;
            public readonly List<(Spell Spell, SpellLine Line)> Vents = new();

            public (Spell Spell, SpellLine Line) PreferredMain =>
                AnimistShroomPlanner.PrefersTauntMain(TauntMain.Spell?.Level, DamageMain.Spell?.Level, Burst.Spell != null)
                    ? TauntMain
                    : DamageMain.Spell != null ? DamageMain : TauntMain;
        }

        // Re-summoning the main shroom costs a 5 s cast, so only move it when
        // the fight has really moved away, and not on every target switch.
        public const long RelocationCooldownMilliseconds = 8_000;
        private const long MainSwapCooldownMilliseconds = 60_000;
        private const long DiagnosticIntervalMilliseconds = 60_000;
        private const int FightScanRadius = 1200;
        private const int PlantSpacing = 700;

        private static readonly Logger Log = LoggerManager.Create(typeof(BotAnimistPolicy));
        private static readonly ConditionalWeakTable<GameBot, State> States = new();
        private static readonly string[] TravelWords = { "travel", "walking", "crossing", "meeting", "returning", "riding" };
        public static bool AppliesTo(GameLiving owner) => owner is GameBot bot &&
            bot.CharacterClass?.ID == (int)eCharacterClass.Animist;

        public static bool ValidEncounter(GameBot bot, GameLiving target) =>
            target != null && target != bot && target.IsAlive && target.ObjectState == GameObject.eObjectState.Active &&
            target.CurrentRegion == bot.CurrentRegion &&
            (target is not GameNPC npc || AutonomousSummonActivity.AutonomousOwner(npc) != bot) &&
            !(target is GameSummonedPet pet && pet.Owner == bot) &&
            GameServer.ServerRules.IsAllowedToAttack(bot, target, true);

        /// <summary>Shrooms are planted this far inside their reach of the target.</summary>
        public const int ShroomReachMargin = 150;

        /// <summary>
        /// How close an Animist stands to its target: inside the reach of its
        /// best main or damage shroom (1,000), so shrooms go up beside it.
        /// Zero when it knows no attacking shroom (no limit then).
        /// </summary>
        public static int ShroomStandoffRange(GameBot bot)
        {
            if (!AppliesTo(bot) || bot.Spells == null)
                return 0;
            int reach = 0;
            foreach (Spell spell in bot.Spells)
            {
                if (spell == null || spell.Level > bot.Level ||
                    spell.SpellType is not (eSpellType.SummonAnimistPet or eSpellType.SummonAnimistFnF or eSpellType.SummonAnimistFnFCustom))
                    continue;
                Spell payload = Payload(spell);
                bool attacks = spell.SpellType == eSpellType.SummonAnimistPet
                    ? AnimistShroomPlanner.ClassifyMainPayload(payload) != AnimistMainKind.None
                    : AnimistShroomPlanner.ClassifyPayload(payload) == AnimistShroomKind.Damage;
                if (attacks)
                    reach = Math.Max(reach, Math.Max(500, payload.Range));
            }
            return reach > 0 ? StandoffFor(reach) : 0;
        }

        public static int StandoffFor(int shroomReach) => Math.Max(300, shroomReach - ShroomReachMargin);

        public static int UsefulRadius(int effectiveRange) => Math.Clamp(effectiveRange > 0 ? effectiveRange : 1500, 750, 2500);

        public static bool IsTraveling(GameBot bot) => IsTraveling(bot, false);

        /// <summary>
        /// During a fight, following the leader or a travel task no longer
        /// counts as travel: a /spawn companion whose player steps around, or a
        /// gamebot attacked on the road, still plants. Physically moving,
        /// riding and recovery always do.
        /// </summary>
        public static bool IsTraveling(GameBot bot, bool fighting)
        {
            if (bot.IsMoving || bot.IsOnHorse || bot.IsOnStableMasterRoute || bot.IsReturningAfterRelease ||
                bot.IsMovingOnPath || bot.CurrentPathPoint != null ||
                AutonomousBotGroupCoordinator.IsInitialMeetup(bot) || AutonomousBotGroupCoordinator.IsRecovering(bot)) return true;
            if (bot.PlayerGroupLeader is GamePlayer leader &&
                (leader.CurrentRegion != bot.CurrentRegion || !bot.IsWithinRadius(leader, fighting ? 2500 : 600) ||
                 !fighting && leader.IsMoving)) return true;
            if (fighting) return false;
            if (bot.IsPlayerLedGroup && bot.Brain?.FSM?.GetCurrentState()?.StateType == eFSMStateType.FOLLOW) return true;
            // A paused travel controller can be stationary while obtaining its
            // next route. Merely selecting a hostile destination is not arrival.
            string activity = bot.PersistentRecord?.Activity ?? string.Empty;
            foreach (string word in TravelWords)
                if (activity.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static void RestoreEncounterTarget(GameBot bot)
        {
            if (bot == null || !States.TryGetValue(bot, out State state) || !state.PendingRestore ||
                bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true) return;
            GameLiving target = state.PendingTarget;
            GameObject castTarget = state.PendingCastTarget ?? bot;
            state.PendingTarget = null;
            state.PendingCastTarget = null;
            state.PendingRestore = false;
            if (ReferenceEquals(bot.TargetObject, castTarget))
                bot.TargetObject = ValidEncounter(bot, target) ? target : null;
        }

        public static bool Maintain(GameBot bot, GameLiving encounterTarget, ref long nextDeployable,
            out string activity, Func<Spell, bool> allowed = null)
        {
            activity = string.Empty;
            RestoreEncounterTarget(bot);
            if (!bot.IsAlive || bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true ||
                bot.IsCrowdControlled || bot.IsRecoveryResting) return false;
            bool encounter = ValidEncounter(bot, encounterTarget);
            if (IsTraveling(bot, encounter)) return false;
            long now = GameLoop.GameLoopTime;
            State state = States.GetOrCreateValue(bot);
            var main = bot.ControlledBrain?.Body as TurretPet;
            if (encounter && main?.IsAlive == true && main.ObjectState == GameObject.eObjectState.Active &&
                bot.ControlledBrain is TurretBrain { IsMainPet: true } && now >= state.NextRelocation)
            {
                int radius = UsefulRadius(main.TurretSpell?.CalculateEffectiveRange(main) ?? 0);
                if ((main.CurrentRegion != bot.CurrentRegion || !main.IsWithinRadius(encounterTarget, radius)) &&
                    InShroomReach(bot, encounterTarget))
                {
                    state.NextRelocation = now + RelocationCooldownMilliseconds;
                    bot.CommandNpcRelease();
                    return false; // The next normal maintenance pass may summon a replacement.
                }
            }

            if (bot.ControlledBrain?.Body is GameNPC old &&
                (!old.IsAlive || old.ObjectState != GameObject.eObjectState.Active))
                bot.CommandNpcRelease();

            if (now < nextDeployable) return false;
            // Enumerate learned spells only when a cast can actually be attempted.
            Kit kit = ReadKit(bot, allowed);
            main = bot.ControlledBrain?.Body as TurretPet;
            bool hasMain = main?.IsAlive == true && main.ObjectState == GameObject.eObjectState.Active;

            if (!encounter)
                return MaintainMainOutOfCombat(bot, kit, main, hasMain, state, now, ref nextDeployable, out activity);

            // The target is out of shroom reach (it moved, or the Animist has
            // not closed yet). Returning lets the ordinary approach move the
            // Animist up; a shroom planted from here would land by the enemy
            // or sit out of range.
            if (!InShroomReach(bot, encounterTarget))
                return false;

            Fight fight = ReadFight(bot, encounterTarget);
            Vector3 damageSpot = BehindSpot(bot, encounterTarget, kit.DamageField.Spell);
            Vector3 ventSpot = GroupSpot(bot, kit.Vents.FirstOrDefault().Spell);
            var ownedShrooms = AutonomousPetSupport.OwnedFieldTurrets(bot)
                .Select(entry => (entry.Turret, entry.ExpiresAt, Kind: AnimistShroomPlanner.ClassifyPayload(entry.Turret.TurretSpell)))
                .ToList();
            int damageNear = ownedShrooms.Count(entry => entry.Kind == AnimistShroomKind.Damage &&
                entry.Turret.IsWithinRadius(encounterTarget, Math.Max(500, entry.Turret.TurretSpell?.CalculateEffectiveRange(entry.Turret) ?? 1000)));
            bool crowded = fight.AwakeHostilesNear(bot, damageSpot, PayloadRange(kit.DamageField.Spell, bot)) >= 2;
            Spell damagePayload = Payload(kit.DamageField.Spell);
            int desired = AnimistShroomPlanner.DesiredDamageShrooms(fight.EngagedHealth, damagePayload?.Damage ?? 0,
                crowded ? AnimistShroomPlanner.CrowdedCampDamageCap : 10);

            (Spell Spell, SpellLine Line) vent = default;
            if (fight.FightWorthBuffing)
            {
                bool group = bot.Group?.MemberCount > 1;
                // Grouped: keep each resist vent up. Solo: one, the highest rank.
                foreach ((Spell Spell, SpellLine Line) candidate in group ? kit.Vents : kit.Vents.Take(1))
                {
                    eSpellType? family = Payload(candidate.Spell)?.SpellType;
                    bool covered = ownedShrooms.Any(entry => entry.Kind == AnimistShroomKind.ResistVent &&
                        entry.Turret.TurretSpell?.SpellType == family &&
                        AnimistShroomPlanner.StillCovers(entry.ExpiresAt, now) &&
                        Vector3.Distance(Position(entry.Turret), ventSpot) <= PlantSpacing);
                    if (!covered && Affordable(bot, candidate.Spell) && CanPlantAt(bot, ventSpot))
                    {
                        vent = candidate;
                        break;
                    }
                }
            }

            GameLiving beingHit = fight.AllyUnderMelee;
            Vector3 ablativeSpot = beingHit != null ? NearSpot(bot, Position(beingHit), kit.Ablative.Spell) : default;
            bool ablativeNeeded = kit.Ablative.Spell != null && beingHit != null &&
                Affordable(bot, kit.Ablative.Spell) && CanPlantAt(bot, ablativeSpot) &&
                !ownedShrooms.Any(entry => entry.Kind == AnimistShroomKind.Ablative &&
                    AnimistShroomPlanner.StillCovers(entry.ExpiresAt, now) &&
                    Vector3.Distance(Position(entry.Turret), ablativeSpot) <= PlantSpacing);

            bool burstReady = kit.Burst.Spell != null && hasMain && Affordable(bot, kit.Burst.Spell) &&
                fight.EngagedNear(main, 350) && fight.AwakeHostilesNear(bot, Position(main), 450, anyCon: true) == 0;

            Vector3 sporeSpot = BehindSpot(bot, encounterTarget, kit.Spore.Spell);
            bool sporeNeeded = kit.Spore.Spell != null && bot.Group?.MemberCount > 1 && beingHit != null &&
                Affordable(bot, kit.Spore.Spell) && CanPlantAt(bot, sporeSpot) &&
                fight.AwakeHostilesNear(bot, Position(encounterTarget), 750, anyCon: true) == 0 &&
                !ownedShrooms.Any(entry => entry.Kind == AnimistShroomKind.MeleeDebuff &&
                    AnimistShroomPlanner.StillCovers(entry.ExpiresAt, now) &&
                    entry.Turret.IsWithinRadius(encounterTarget, 1000));

            var situation = new AnimistShroomPlanner.Situation(
                HasMain: hasMain,
                CanSummonMain: kit.PreferredMain.Spell != null && AutonomousPetSupport.CanCast(bot, kit.PreferredMain.Spell),
                MainHealthPercent: hasMain ? main.HealthPercent : 100,
                CanHealMain: kit.HealMain.Spell != null && AutonomousPetSupport.CanCast(bot, kit.HealMain.Spell) &&
                             hasMain && bot.IsWithinRadius(main, kit.HealMain.Spell.CalculateEffectiveRange(bot)),
                TargetDying: fight.Engaged.Count <= 1 && encounterTarget.HealthPercent < 15,
                FightWorthBuffing: fight.FightWorthBuffing,
                DamageShroomsNearFight: damageNear,
                DesiredDamageShrooms: desired,
                CanPlantDamage: kit.DamageField.Spell != null && Affordable(bot, kit.DamageField.Spell) && CanPlantAt(bot, damageSpot),
                VentNeeded: vent.Spell != null,
                AblativeNeeded: ablativeNeeded,
                BurstReady: burstReady,
                SporeNeeded: sporeNeeded);

            AnimistShroomAction action = AnimistShroomPlanner.Next(situation);
            LogPeriodically(bot, state, now, kit, ownedShrooms.Select(entry => entry.Kind), desired, crowded, action);

            bool cast = action switch
            {
                AnimistShroomAction.SummonMain => Cast(bot, encounterTarget, kit.PreferredMain, bot,
                    BehindSpot(bot, encounterTarget, kit.PreferredMain.Spell)),
                AnimistShroomAction.HealMain => Cast(bot, encounterTarget, kit.HealMain, main, null),
                AnimistShroomAction.PlantDamage => Cast(bot, encounterTarget, kit.DamageField, bot, damageSpot),
                AnimistShroomAction.PlantVent => Cast(bot, encounterTarget, vent, bot, ventSpot),
                AnimistShroomAction.PlantAblative => Cast(bot, encounterTarget, kit.Ablative, bot, ablativeSpot),
                AnimistShroomAction.TurretBurst => Cast(bot, encounterTarget, kit.Burst, main, null),
                AnimistShroomAction.PlantSpore => Cast(bot, encounterTarget, kit.Spore, bot, sporeSpot),
                _ => false
            };
            if (!cast)
                return false;

            Spell used = action switch
            {
                AnimistShroomAction.SummonMain => kit.PreferredMain.Spell,
                AnimistShroomAction.HealMain => kit.HealMain.Spell,
                AnimistShroomAction.PlantDamage => kit.DamageField.Spell,
                AnimistShroomAction.PlantVent => vent.Spell,
                AnimistShroomAction.PlantAblative => kit.Ablative.Spell,
                AnimistShroomAction.TurretBurst => kit.Burst.Spell,
                _ => kit.Spore.Spell
            };
            // The cast itself is the limit; no extra AI pause between legal casts.
            nextDeployable = now + Math.Max(750, used.CastTime + 250);
            if (action == AnimistShroomAction.SummonMain)
                state.NextRelocation = now + RelocationCooldownMilliseconds;
            activity = action switch
            {
                AnimistShroomAction.SummonMain => $"Deploying main turret: {used.Name}",
                AnimistShroomAction.HealMain => $"Healing main turret: {used.Name}",
                AnimistShroomAction.TurretBurst => $"Turret burst: {used.Name}",
                _ => $"Planting field turret: {used.Name}"
            };
            return true;
        }

        private static bool MaintainMainOutOfCombat(GameBot bot, Kit kit, TurretPet main, bool hasMain, State state,
            long now, ref long nextDeployable, out string activity)
        {
            activity = string.Empty;
            (Spell Spell, SpellLine Line) preferred = kit.PreferredMain;
            if (preferred.Spell == null)
                return false;

            if (bot.ControlledBrain == null)
            {
                if (!AutonomousPetSupport.CanCast(bot, preferred.Spell) || !Cast(bot, null, preferred, bot, null, mainNearFight: true))
                    return false;
                nextDeployable = now + Math.Max(1500, preferred.Spell.CastTime + 500);
                state.NextRelocation = now + RelocationCooldownMilliseconds;
                activity = $"Deploying main turret: {preferred.Spell.Name}";
                return true;
            }

            // Between fights, swap a main shroom of the wrong kind (or an
            // outgrown rank) for the preferred one, with power to spare.
            if (hasMain && bot.ControlledBrain is TurretBrain { IsMainPet: true } && !bot.InCombat &&
                now >= state.NextMainSwap && bot.MaxMana > 0 && bot.Mana * 100 >= bot.MaxMana * 60 &&
                main.TurretSpell is Spell current && Payload(preferred.Spell) is Spell wanted &&
                current.ID != wanted.ID && !string.Equals(current.Name, wanted.Name, StringComparison.OrdinalIgnoreCase))
            {
                state.NextMainSwap = now + MainSwapCooldownMilliseconds;
                bot.CommandNpcRelease();
                activity = $"Replacing main turret with {preferred.Spell.Name}";
                return true;
            }
            return false;
        }

        private static Kit ReadKit(GameBot bot, Func<Spell, bool> allowed)
        {
            var kit = new Kit();
            var vents = new Dictionary<eSpellType, (Spell Spell, SpellLine Line)>();
            foreach ((Spell Spell, SpellLine Line) entry in AutonomousPetSupport.KnownSpells(bot))
            {
                Spell spell = entry.Spell;
                if (spell == null || spell.Level > bot.Level || allowed != null && !allowed(spell))
                    continue;
                Spell payload = Payload(spell);
                switch (spell.SpellType)
                {
                    case eSpellType.SummonAnimistPet:
                        switch (AnimistShroomPlanner.ClassifyMainPayload(payload))
                        {
                            case AnimistMainKind.Damage: Keep(ref kit.DamageMain, entry); break;
                            case AnimistMainKind.Taunt: Keep(ref kit.TauntMain, entry); break;
                        }
                        break;
                    case eSpellType.SummonAnimistFnF:
                    case eSpellType.SummonAnimistFnFCustom:
                        switch (AnimistShroomPlanner.ClassifyPayload(payload))
                        {
                            case AnimistShroomKind.Damage: Keep(ref kit.DamageField, entry); break;
                            case AnimistShroomKind.Ablative: Keep(ref kit.Ablative, entry); break;
                            case AnimistShroomKind.MeleeDebuff: Keep(ref kit.Spore, entry); break;
                            case AnimistShroomKind.ResistVent:
                                vents.TryGetValue(payload.SpellType, out var best);
                                Keep(ref best, entry);
                                vents[payload.SpellType] = best;
                                break;
                            // Tanglers (snare) are left to players: a 5 s cast
                            // snare is slower than the pull it would slow.
                        }
                        break;
                    case eSpellType.TurretPBAoE:
                        Keep(ref kit.Burst, entry);
                        break;
                    case eSpellType.Bomber when spell.Target == eSpellTarget.PET && payload?.SpellType == eSpellType.Heal:
                        Keep(ref kit.HealMain, entry);
                        break;
                }
            }
            kit.Vents.AddRange(vents.Values.OrderByDescending(entry => entry.Spell.Level).ThenBy(entry => entry.Spell.ID));
            return kit;
        }

        private static void Keep(ref (Spell Spell, SpellLine Line) best, (Spell Spell, SpellLine Line) candidate)
        {
            if (best.Spell == null || candidate.Spell.Level > best.Spell.Level ||
                candidate.Spell.Level == best.Spell.Level && candidate.Spell.ID > best.Spell.ID)
                best = candidate;
        }

        private static Spell Payload(Spell summon) =>
            summon?.SubSpellID > 0 ? SkillBase.GetSpellByID(summon.SubSpellID) : null;

        private static int PayloadRange(Spell summon, GameLiving caster) =>
            Math.Max(500, Payload(summon)?.CalculateEffectiveRange(caster) ?? 1000);

        private static bool InShroomReach(GameBot bot, GameLiving target)
        {
            int standoff = ShroomStandoffRange(bot);
            return standoff <= 0 || bot.IsWithinRadius(target, standoff + 100);
        }

        private static bool Affordable(GameBot bot, Spell spell) =>
            spell != null && AutonomousPetSupport.CanCast(bot, spell) &&
            AnimistShroomPlanner.KeepsReserve(bot.Mana, bot.MaxMana, bot.PowerCost(spell));

        private static bool CanPlantAt(GameBot bot, Vector3 spot) =>
            AutonomousPetSupport.CanDeployFieldTurretAt(bot, new Point3D((int)spot.X, (int)spot.Y, (int)spot.Z));

        private static Vector3 Position(GameObject obj) => new(obj.X, obj.Y, obj.Z);

        /// <summary>
        /// Period advice: plant damage shrooms on the Animist's side, behind it,
        /// not in the camp, so they reach the fight but wake as little else as
        /// possible. Still inside the shroom's reach of the target.
        /// </summary>
        private static Vector3 BehindSpot(GameBot bot, GameLiving target, Spell summon)
        {
            Vector3 owner = Position(bot);
            Vector3 enemy = Position(target);
            Vector3 away = owner - enemy;
            away = away.LengthSquared() < 1 ? Vector3.UnitX : Vector3.Normalize(away);
            Vector3 desired = owner + away * 90 + Spread(30, 90);
            int reach = PayloadRange(summon, bot) - 120;
            if (Vector3.Distance(desired, enemy) > reach)
                desired = enemy + Vector3.Normalize(desired - enemy) * reach;
            return ClampToCastRange(bot, desired, summon);
        }

        /// <summary>Resist vents buff within 350 of themselves: plant them among the group.</summary>
        private static Vector3 GroupSpot(GameBot bot, Spell summon)
        {
            Vector3 sum = Position(bot);
            int count = 1;
            if (bot.Group?.MemberCount > 1)
                foreach (GameLiving member in bot.Group.GetMembersInTheGroup())
                    if (member != bot && member?.IsAlive == true && member.CurrentRegion == bot.CurrentRegion &&
                        bot.IsWithinRadius(member, 1500))
                    {
                        sum += Position(member);
                        count++;
                    }
            return NearSpot(bot, sum / count, summon);
        }

        private static Vector3 NearSpot(GameBot bot, Vector3 point, Spell summon) =>
            ClampToCastRange(bot, point + Spread(30, 80), summon);

        private static Vector3 ClampToCastRange(GameBot bot, Vector3 desired, Spell summon)
        {
            Vector3 owner = Position(bot);
            int range = Math.Max(100, summon?.CalculateEffectiveRange(bot) ?? 1000) - 40;
            return Vector3.Distance(owner, desired) > range
                ? owner + Vector3.Normalize(desired - owner) * range
                : desired;
        }

        private static Vector3 Spread(int min, int max)
        {
            // Never stack shrooms on one coordinate: collision and LoS failures
            // stay independent instead of repeating one bad placement.
            double angle = Random.Shared.NextDouble() * Math.PI * 2;
            float radius = Random.Shared.Next(min, max + 1);
            return new((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
        }

        /// <summary>The monsters already fighting this bot's side near the encounter.</summary>
        private sealed class Fight
        {
            public readonly List<GameNPC> Engaged = new();
            public readonly List<GameNPC> Awake = new();
            public long EngagedHealth;
            public GameLiving AllyUnderMelee;
            public bool FightWorthBuffing;

            public bool EngagedNear(GameObject point, int radius) => Engaged.Any(npc => npc.IsWithinRadius(point, radius));

            /// <summary>
            /// Hostile monsters near a spot that are not in this fight. Shrooms
            /// attack anything non-grey in reach; a burst or spore hits every con.
            /// </summary>
            public int AwakeHostilesNear(GameBot bot, Vector3 spot, int radius, bool anyCon = false) =>
                Awake.Count(npc => (anyCon || !bot.IsObjectGreyCon(npc)) &&
                    Vector3.DistanceSquared(Position(npc), spot) <= (float)radius * radius);
        }

        private static Fight ReadFight(GameBot bot, GameLiving target)
        {
            var fight = new Fight();
            if (target is GameNPC targetNpc)
                fight.Engaged.Add(targetNpc);
            foreach (GameNPC npc in target.GetNPCsInRadius(FightScanRadius))
            {
                if (npc == null || npc == target || !npc.IsAlive || npc.ObjectState != GameObject.eObjectState.Active ||
                    npc.Brain is IControlledBrain || !GameServer.ServerRules.IsAllowedToAttack(bot, npc, true))
                    continue;
                if (npc.InCombat && npc.TargetObject is GameLiving victim && OnOurSide(bot, victim))
                    fight.Engaged.Add(npc);
                else if (!npc.InCombat)
                    fight.Awake.Add(npc);
            }
            foreach (GameNPC npc in fight.Engaged)
            {
                fight.EngagedHealth += Math.Max(0, npc.Health);
                if (fight.AllyUnderMelee == null && npc.TargetObject is GameLiving victim && OnOurSide(bot, victim) &&
                    npc.IsWithinRadius(victim, 400))
                    fight.AllyUnderMelee = victim;
            }
            if (target is not GameNPC)
                fight.EngagedHealth += Math.Max(0, target.Health);
            fight.FightWorthBuffing = fight.Engaged.Count > 1 || target.HealthPercent >= 40;
            return fight;
        }

        private static bool OnOurSide(GameBot bot, GameLiving living)
        {
            if (living == null)
                return false;
            if (living == bot || living == bot.ControlledBrain?.Body)
                return true;
            if (bot.Group != null && living.Group == bot.Group)
                return true;
            GameLiving owner = (living as GameNPC)?.Brain is IControlledBrain brain ? brain.Owner : null;
            return owner != null && (owner == bot || bot.Group != null && owner.Group == bot.Group);
        }

        private static void LogPeriodically(GameBot bot, State state, long now, Kit kit,
            IEnumerable<AnimistShroomKind> owned, int desired, bool crowded, AnimistShroomAction action)
        {
            // Temporary in-game check of the shroom rework; one line per bot per minute in fights.
            if (now < state.NextDiagnostic)
                return;
            state.NextDiagnostic = now + DiagnosticIntervalMilliseconds;
            try
            {
                var counts = owned.GroupBy(kind => kind).ToDictionary(group => group.Key, group => group.Count());
                int Count(AnimistShroomKind kind) => counts.TryGetValue(kind, out int value) ? value : 0;
                string main = (bot.ControlledBrain?.Body as TurretPet)?.TurretSpell is Spell payload
                    ? AnimistShroomPlanner.ClassifyMainPayload(payload).ToString() : "none";
                Log.Info($"ANIMIST_SHROOMS bot={bot.Name} kind={(bot.IsAutonomousWorldBot ? "gamebot" : "companion")} " +
                         $"level={bot.Level} group={bot.Group?.MemberCount ?? 1} main={main} " +
                         $"preferred={kit.PreferredMain.Spell?.Name ?? "none"} damage={Count(AnimistShroomKind.Damage)}/{desired} " +
                         $"vents={Count(AnimistShroomKind.ResistVent)} ablative={Count(AnimistShroomKind.Ablative)} " +
                         $"spores={Count(AnimistShroomKind.MeleeDebuff)} crowded={crowded} next={action} " +
                         $"mana={bot.ManaPercent}%");
            }
            catch
            {
            }
        }

        internal static void FinishSummonTarget(GameBot bot, GameLiving target, GameObject previous, bool accepted) =>
            FinishCastTarget(bot, target, bot, previous, accepted);

        private static void FinishCastTarget(GameBot bot, GameLiving target, GameObject castTarget, GameObject previous, bool accepted)
        {
            if (accepted && (bot.IsCasting || bot.castingComponent?.HasPendingSkillRequests == true))
            {
                State state = States.GetOrCreateValue(bot);
                state.PendingTarget = target;
                state.PendingCastTarget = castTarget;
                state.PendingRestore = true;
            }
            else if (ReferenceEquals(bot.TargetObject, castTarget))
                bot.TargetObject = ValidEncounter(bot, target) ? target :
                    previous is GameLiving living ? (ValidEncounter(bot, living) ? living : null) : previous;
        }

        private static bool Cast(GameBot bot, GameLiving target, (Spell Spell, SpellLine Line) entry,
            GameLiving castTarget, Vector3? groundSpot, bool mainNearFight = false)
        {
            if (entry.Spell == null || castTarget == null)
                return false;
            if (groundSpot is Vector3 spot)
                AutonomousPetSupport.SetAnimistGroundTarget(bot, spot);
            else if (mainNearFight)
                AutonomousPetSupport.PrepareAnimistGroundTarget(bot, target, entry.Spell);
            GameObject previous = bot.TargetObject;
            bot.TargetObject = castTarget;
            bool accepted = bot.CastSpell(entry.Spell, entry.Line);
            FinishCastTarget(bot, target, castTarget, previous, accepted);
            return accepted;
        }
    }
}
