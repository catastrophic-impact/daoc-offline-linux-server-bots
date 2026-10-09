using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Spells;
using DOL.GS.ServerProperties;

namespace DOL.GS;

/// <summary>
/// Shared pet upkeep for autonomous world bots and an opted-in player. It only
/// casts spells actually known by the character and uses the normal pet brains.
/// </summary>
public static class AutonomousPetSupport
{
    public const string SyntheticCharmPetProperty = "autonomous.synthetic.charm.pet";
    internal const string BonedancerSpellRefreshProperty = "autonomous.bonedancer.minion.refresh.level";
    private const string BonedancerDesiredMinionCountProperty = "autonomous.bonedancer.minion.desired.count";
    private static readonly SpellLine MobSpellLine = SkillBase.GetSpellLine(GlobalSpellsLines.Mob_Spells);
    private static readonly ConcurrentDictionary<(int Level, ushort CharmType, eCharacterClass Class), DbMob[]> CharmTemplates = new();
    private static readonly ConcurrentDictionary<(int Level, ushort CharmType, eCharacterClass Class), DbMob[]> PlayerCharmTemplates = new();
    private static readonly ConcurrentDictionary<GameLiving, PendingCharm> PendingCharms = new();
    private static readonly ConcurrentDictionary<GameLiving, ActiveSyntheticCharm> ActiveSyntheticCharms = new();
    // Value: the game-loop tick at which the field turret's summon expires.
    private static readonly ConcurrentDictionary<GameLiving, ConcurrentDictionary<TurretPet, long>> FieldTurrets = new();
    private static readonly ConcurrentDictionary<eCharacterClass, Spell> GeneratedCharmSpells = new();
    private static readonly ConcurrentDictionary<(eCharacterClass Class, int SpellId), Spell> PlayerGeneratedCharmSpells = new();
    private static readonly ConditionalWeakTable<GameBot, BonedancerMinionSpellCache> BonedancerMinionSpells = new();
    private static readonly ConditionalWeakTable<GameNPC, CovenantPetBuffCadenceState> CovenantPetBuffCadences = new();
    private sealed record PendingCharm(GameNPC Mob, long ExpiresAtTick);
    private sealed class PermanentGeneratedCharmSpell : Spell
    {
        public PermanentGeneratedCharmSpell(Spell source) : base(source, eSpellType.Charm)
        {
            Duration = ushort.MaxValue * 1000;
            Value = 52;
            Damage = 100;
        }

        public override int Pulse => 0;
        public override int Frequency => 0;
    }
    private sealed class ActiveSyntheticCharm(GameNPC mob)
    {
        public GameNPC Mob { get; } = mob;
        public long MissingBrainSinceTick { get; set; }
    }
    private sealed class BonedancerMinionSpellCache
    {
        public bool Initialized;
        public int Fingerprint;
        public List<(Spell Spell, SpellLine Line)> Spells = [];
    }

    /// <summary>
    /// A successful Covenant cast can proceed to a different pet buff as soon
    /// as its effect appears. A cast whose effect never appears keeps the old
    /// retry window, and combat keeps the old spacing between routine buffs.
    /// </summary>
    public sealed class CovenantPetBuffCadenceState
    {
        private readonly object _sync = new();
        private eEffect _pendingEffect;
        private int _pendingSpellId;
        private long _retryUntil;
        private long _combatBuffUntil;

        public (eEffect Effect, int SpellId) PendingSpell
        {
            get
            {
                lock (_sync)
                    return (_pendingEffect, _pendingSpellId);
            }
        }

        public void Record(eEffect effect, int spellId, long retryUntil)
        {
            lock (_sync)
            {
                _pendingEffect = effect;
                _pendingSpellId = spellId;
                _retryUntil = retryUntil;
                _combatBuffUntil = retryUntil;
            }
        }

        public bool IsDeferred(long now, bool combatOwnsTurn, bool pendingEffectPresent)
        {
            lock (_sync)
            {
                if (combatOwnsTurn && now < _combatBuffUntil)
                    return true;
                return _pendingEffect != eEffect.Unknown && !pendingEffectPresent && now < _retryUntil;
            }
        }
    }

    public readonly record struct GeneratedCharmProfile(
        int UnlockLevel,
        int PetLevelOffset,
        int ClientEffect,
        int CastTimeMilliseconds,
        string SpellName);

    /// <summary>
    /// Capital summoning remains blocked for real players. Server-side GameBots
    /// (persistent characters and temporary companions) are the sole exception,
    /// and their controlled brains are kept command-only by
    /// <see cref="ApplyCommandOnlyPolicy"/>.
    /// </summary>
    public static bool CanSummonInCapital(GameLiving owner) =>
        owner is GameBot;

    /// <summary>
    /// These classes use a controlled pet while the owner remains an
    /// independently moving and casting combatant. Pet upkeep must never own or
    /// consume the owner's AI turn once the main pet exists.
    /// </summary>
    public static bool UsesIndependentOwnerPetCombat(eCharacterClass characterClass) =>
        characterClass is eCharacterClass.Enchanter or eCharacterClass.Cabalist or
            eCharacterClass.Spiritmaster or eCharacterClass.Sorcerer or eCharacterClass.Mentalist or
            eCharacterClass.Hunter;

    /// <summary>
    /// Classes whose PET/CONTROLLED spell lifecycle is owned exclusively by
    /// this service. Bonedancers retain their recursive commander-tree combat
    /// policy, but their owner-side pet spells must not fall through to the
    /// generic defensive selector (which treats several pet buffs as self
    /// buffs and can starve FOLLOW forever after the army is summoned).
    /// </summary>
    public static bool OwnsPetUpkeep(eCharacterClass characterClass) =>
        characterClass is eCharacterClass.Bonedancer or eCharacterClass.Necromancer or eCharacterClass.Minstrel or
            eCharacterClass.Sluaghbinder ||
        UsesIndependentOwnerPetCombat(characterClass);

    /// <summary>
    /// Returns the live enemy a commanded pet tree is already fighting. This
    /// lets a persistent owner recover a lost combat handoff without changing
    /// companion orders or allowing pets to acquire targets on their own.
    /// </summary>
    public static GameLiving ActiveOwnedPetCombatTarget(GameBot owner)
    {
        if (owner?.ControlledBrain == null)
            return null;

        return ActivePetCombatTarget(owner, owner.ControlledBrain, 0);
    }

    private static GameLiving ActivePetCombatTarget(GameBot owner, IControlledBrain brain, int depth)
    {
        if (owner == null || brain?.Body == null || depth > 3)
            return null;

        GameLiving target = brain is ControlledMobBrain controlled
            ? controlled.OrderedAttackTarget
            : null;
        if (target == null && (brain.Body.InCombat || brain.Body.IsAttacking))
            target = brain.Body.TargetObject as GameLiving;

        if (target?.IsAlive == true && target.ObjectState is GameObject.eObjectState.Active &&
            target.CurrentRegionID == owner.CurrentRegionID && owner.IsWithinRadius(target, 6000) &&
            GameServer.ServerRules.IsAllowedToAttack(owner, target, true))
        {
            return target;
        }

        foreach (IControlledBrain child in brain.Body.ControlledNpcList ?? [])
        {
            target = ActivePetCombatTarget(owner, child, depth + 1);
            if (target != null)
                return target;
        }

        return null;
    }

    /// <summary>
    /// Bot-only generated charm policy. It deliberately does not alter normal
    /// player spell acquisition or the native pulsing Mentalist charm.
    /// </summary>
    public static bool TryGetGeneratedCharmProfile(eCharacterClass characterClass, out GeneratedCharmProfile profile)
    {
        profile = characterClass switch
        {
            // Preserve the existing Sorcerer unlock while making the generated
            // companion independent of the bot's randomly chosen specialization.
            eCharacterClass.Sorcerer => new GeneratedCharmProfile(7, 2, 951, 4_000, "Bound Sorcerer Companion"),
            // Illusory Enemy is the first 1.65 Mentalist charm, at rank 4.
            eCharacterClass.Mentalist => new GeneratedCharmProfile(4, 3, 4211, 3_000, "Bound Illusory Companion"),
            // Minstrels receive this helper at level 6 regardless of their
            // randomly chosen weapon split. It remains defensive and does not
            // replace any of the class's original song rotation.
            eCharacterClass.Minstrel => new GeneratedCharmProfile(6, 3, 911, 0, "Bound Minstrel Companion"),
            _ => default
        };
        return profile.UnlockLevel > 0;
    }

    public static int GeneratedCharmTargetLevel(eCharacterClass characterClass, int ownerLevel)
    {
        if (!TryGetGeneratedCharmProfile(characterClass, out GeneratedCharmProfile profile) ||
            ownerLevel < profile.UnlockLevel)
            return 0;
        return Math.Max(1, ownerLevel - profile.PetLevelOffset);
    }

    /// <summary>
    /// The local player's charm button may create the same realm-authentic,
    /// level-scaled companion used by GameBots. The learned spell still owns
    /// its normal cast time, power cost, animation, and body-type restriction;
    /// only the generated candidate and permanent/no-resist lifecycle differ.
    /// Existing controlled pets and capital cities are intentionally excluded.
    /// </summary>
    public static bool TryPreparePlayerGeneratedCharm(
        GamePlayer owner,
        Spell learnedCharm,
        DbMob selectedTemplate,
        out GameNPC candidate,
        out Spell permanentCharm)
    {
        candidate = null;
        permanentCharm = null;
        if (owner == null || owner is GameBot || selectedTemplate == null || learnedCharm?.SpellType != eSpellType.Charm ||
            owner.ControlledBrain != null || owner.CurrentRegion?.IsCapitalCity == true ||
            !CanCreateSyntheticCharmInRegion(owner.CurrentRegion?.IsCapitalCity == true))
            return false;

        eCharacterClass characterClass = (eCharacterClass)owner.CharacterClass.ID;
        if (!PlayerGeneratedCharmPolicy.TryGetRank(characterClass, learnedCharm.ID,
                out int unlockLevel, out int levelBonus) || owner.Level < unlockLevel)
            return false;
        GeneratedCharmProfile profile = new(unlockLevel, -levelBonus,
            learnedCharm.ClientEffect, learnedCharm.CastTime, learnedCharm.Name);

        permanentCharm = PlayerGeneratedCharmSpells.GetOrAdd(
            (characterClass, learnedCharm.ID), _ => new PermanentGeneratedCharmSpell(learnedCharm));
        if (!TryCreateCharmCandidate(owner, learnedCharm, profile, characterClass, out candidate, true, selectedTemplate))
        {
            permanentCharm = null;
            return false;
        }

        GameNPC cleanupCandidate = candidate;
        GamePlayer cleanupOwner = owner;
        PendingCharm pending = new(cleanupCandidate,
            GameLoop.GameLoopTime + Math.Max(5_000, permanentCharm.CastTime + 3_000));
        if (!PendingCharms.TryAdd(owner, pending))
        {
            DeleteSyntheticCharm(cleanupCandidate);
            candidate = null;
            permanentCharm = null;
            return false;
        }
        _ = new ECSGameTimer(cleanupCandidate, timer =>
        {
            if (PendingCharms.TryGetValue(cleanupOwner, out PendingCharm current) &&
                current.Mob == cleanupCandidate)
                PendingCharms.TryRemove(cleanupOwner, out _);
            if (cleanupOwner.ControlledBrain?.Body != cleanupCandidate)
                DeleteSyntheticCharm(cleanupCandidate);
            return 0;
        }, Math.Max(5_000, permanentCharm.CastTime + 3_000));
        return true;
    }

    public static void CompleteSyntheticCharm(GameLiving owner, GameNPC mob)
    {
        if (owner == null || mob == null ||
            mob.TempProperties.GetProperty<bool>(SyntheticCharmPetProperty) != true)
            return;

        if (PendingCharms.TryGetValue(owner, out PendingCharm pending) && pending.Mob == mob)
            PendingCharms.TryRemove(owner, out _);
        if (owner is GameBot)
            ActiveSyntheticCharms[owner] = new ActiveSyntheticCharm(mob);
    }

    public static void AbandonPendingSyntheticCharm(GameLiving owner, GameNPC mob)
    {
        if (owner != null && PendingCharms.TryGetValue(owner, out PendingCharm pending) && pending.Mob == mob)
            PendingCharms.TryRemove(owner, out _);
        DeleteSyntheticCharm(mob);
    }

    /// <summary>
    /// Synthetic charm candidates are field replacements, not capital NPCs.
    /// This deliberately does not block normal class summons or an existing
    /// controlled pet that follows its owner through a city.
    /// </summary>
    public static bool CanCreateSyntheticCharmInRegion(bool isCapitalCity) => !isCapitalCity;

    internal static int SyntheticCharmRespawnInterval(int inheritedInterval, bool gameBotOwner) =>
        gameBotOwner ? -1 : inheritedInterval;

    internal static bool IsExpiredSyntheticCharmBinding(long now, long unboundSinceTick) =>
        unboundSinceTick != 0 && now - unboundSinceTick >= 2_000;

    public static bool Maintain(
        GameLiving owner,
        GameLiving combatTarget,
        ref long nextDeployablePetTick,
        out string activity,
        Func<Spell, bool> spellAllowed = null)
    {
        if (owner is GameBot animist && BotAnimistPolicy.AppliesTo(animist))
            return BotAnimistPolicy.Maintain(animist, combatTarget, ref nextDeployablePetTick, out activity, spellAllowed);
        activity = string.Empty;
        long now = GameLoop.GameLoopTime;
        PruneDistantFieldTurrets(owner);
        if (owner != null && ReconcileActiveSyntheticCharm(owner, now, ref nextDeployablePetTick, out activity))
            return true;
        if (owner != null && PendingCharms.TryGetValue(owner, out PendingCharm pending))
        {
            GameNPC controlled = owner.ControlledBrain?.Body;
            bool candidateInvalid = pending.Mob == null || !pending.Mob.IsAlive ||
                                    pending.Mob.ObjectState is not GameObject.eObjectState.Active ||
                                    pending.Mob.CurrentRegion != owner.CurrentRegion;
            if (!candidateInvalid && controlled == pending.Mob)
            {
                PendingCharms.TryRemove(owner, out _);
                ActiveSyntheticCharms[owner] = new ActiveSyntheticCharm(pending.Mob);
            }
            else if (candidateInvalid || controlled != null || now >= pending.ExpiresAtTick && !owner.IsCasting)
            {
                PendingCharms.TryRemove(owner, out _);
                DeleteSyntheticCharm(pending.Mob);
                nextDeployablePetTick = Math.Max(nextDeployablePetTick, now + 3_000);
            }
            else
            {
                activity = $"Charming {pending.Mob.Name}";
                return true;
            }
        }

        if (owner == null || !owner.IsAlive || owner.IsCasting || owner.IsCrowdControlled)
            return false;

        IGamePlayer playerLike = owner as IGamePlayer;
        if (playerLike == null)
            return false;
        eCharacterClass characterClass = (eCharacterClass)playerLike.CharacterClass.ID;
        // Older bots were created with mixed Bonedancer ratios.  Migrate that
        // policy on their next ordinary turn without changing any already
        // trained specialization levels, so both /spawn helpers and persistent
        // player bots continue from their current build safely.
        if (owner is GameBot bonedancerPlanBot && characterClass == eCharacterClass.Bonedancer)
            bonedancerPlanBot.EnsureBonedancerFocusedSpecPlan();

        bool defensiveOnlyPet = IsDefensiveOnlyPetClass(characterClass, owner is GameBot && owner.Group?.MemberCount > 1) ||
                                owner is not GameBot && characterClass == eCharacterClass.Mentalist;

        IControlledBrain petBrain = owner.ControlledBrain;
        if (petBrain?.Body != null && (!petBrain.Body.IsAlive || petBrain.Body.ObjectState is not GameObject.eObjectState.Active))
        {
            playerLike.CommandNpcRelease();
            petBrain = null;
        }

        // A Necromancer's shade effect is normally ended by the native pet
        // release path. If the pet was removed by death cleanup first, however,
        // ControlledBrain can already be null and the shade flag otherwise
        // survives indefinitely. Explicitly close that orphaned shade before
        // the owner resumes ordinary combat/travel decisions.
        if (petBrain == null && playerLike.CharacterClass?.ID == (int)eCharacterClass.Necromancer && playerLike.IsShade)
            playerLike.Shade(false);

        IReadOnlyList<(Spell Spell, SpellLine Line)> spells = owner is GameBot cachedOwner && spellAllowed == null
            ? BotAutomatedSpells(cachedOwner)
            : KnownSpells(owner)
                .Where(entry => spellAllowed == null || spellAllowed(entry.Spell))
                .Where(entry => AnimistSingleTargetPolicy.AllowsAutomatedSpell(entry.Spell))
                .ToList();

        // Bonedancer commanders are upgraded as new commander ranks become
        // available. The normal player flow replaces the old commander when
        // a new rank is learned, but bots previously kept the level-one
        // Returned Commander forever. That commander has no subordinate
        // slots, so minion upkeep could never summon a skeleton. Only
        // GameBots use this autonomous replacement; ordinary players and
        // their manually controlled pets are untouched.
        if (owner is GameBot bonedancerBot &&
            characterClass == eCharacterClass.Bonedancer &&
            petBrain?.Body is CommanderPet commander &&
            ShouldUpgradeBonedancerCommander(bonedancerBot, commander, spells, combatTarget))
        {
            playerLike.CommandNpcRelease();
            nextDeployablePetTick = now + 1_500;
            activity = $"Replacing Bonedancer commander ({commander.CommanderType})";
            return true;
        }

        if (petBrain == null)
        {
            eCharacterClass ownerClass = characterClass;
            bool ownerHasAggro = owner is GameBot { Brain: BotBrain botBrain } && botBrain.HasAggro;
            GeneratedCharmProfile charmProfile = default;
            bool mayCreateSyntheticCharm = owner is GameBot &&
                                           CanCreateSyntheticCharmInRegion(owner.CurrentRegion?.IsCapitalCity == true) &&
                                           !owner.InCombat && !owner.IsAttacking && !ownerHasAggro &&
                                           TryGetGeneratedCharmProfile(ownerClass, out charmProfile) &&
                                           owner.Level >= charmProfile.UnlockLevel;
            Spell generatedCharm = null;
            if (mayCreateSyntheticCharm)
            {
                generatedCharm = ownerClass == eCharacterClass.Sorcerer
                    ? spells.Where(entry => entry.Spell.SpellType == eSpellType.Charm &&
                                             CanCast(owner, entry.Spell) &&
                                             GeneratedCharmTargetLevel(ownerClass, owner.Level) <= entry.Spell.Value)
                        .OrderByDescending(entry => entry.Spell.Value)
                        .ThenByDescending(entry => entry.Spell.Level)
                        .Select(entry => entry.Spell)
                        .FirstOrDefault()
                    : GeneratedCharmSpells.GetOrAdd(ownerClass, _ => CreateGeneratedCharmSpell(charmProfile));
            }
            if (generatedCharm != null && now >= nextDeployablePetTick && CanCast(owner, generatedCharm) &&
                TryCreateCharmCandidate(owner, generatedCharm, charmProfile, ownerClass, out GameNPC charmTarget))
            {
                var pendingCandidate = new PendingCharm(charmTarget,
                    now + Math.Max(5_000, generatedCharm.CastTime + 3_000));
                bool registered = PendingCharms.TryAdd(owner, pendingCandidate);
                if (registered && CastPreservingTarget(owner, charmTarget, generatedCharm, MobSpellLine))
                {
                    activity = $"Charming {charmTarget.Name}";
                    return true;
                }
                if (registered)
                    PendingCharms.TryRemove(owner, out _);
                DeleteSyntheticCharm(charmTarget);
                nextDeployablePetTick = now + 3_000;
            }

            (Spell Spell, SpellLine Line) summon = ChooseWeightedByRank(
                spells.Where(entry => IsMainPetSummon(entry.Spell.SpellType) && CanCast(owner, entry.Spell)),
                owner is GameBot { IsEndgameCompanion: true });
            if (summon.Spell != null)
            {
                PrepareAnimistGroundTarget(owner, combatTarget, summon.Spell);
                if (CastPreservingTarget(owner, owner, summon.Spell, summon.Line))
                {
                    nextDeployablePetTick = now + PetActionCooldown(summon.Spell);
                    activity = $"Summoning {summon.Spell.Name}";
                    return true;
                }
            }
        }

        petBrain = owner.ControlledBrain;
        GameNPC pet = petBrain?.Body;
        if (pet != null)
        {
            bool petActionReady = now >= nextDeployablePetTick &&
                (petBrain is not NecromancerPetBrain servant || !servant.HasPendingBotCommand);
            bool capitalSafety = ApplyCommandOnlyPolicy(owner, petBrain);
            if (capitalSafety)
                combatTarget = null;
            bool independentOwnerCombat = owner is GameBot && UsesIndependentOwnerPetCombat(characterClass);

            // A pet can retain the just-killed target in its controlled-brain
            // aggro list for a few pulses. That stale order used to keep the
            // owner and pet in combat forever, so the owner never resumed its
            // route or formation. Clear only a genuinely dead/empty pet target
            // while the owner itself is idle; live retaliation is preserved.
            ClearStalePetCombat(owner, petBrain, combatTarget, defensiveOnlyPet);

            if (independentOwnerCombat)
                SynchronizeIndependentPet(owner, petBrain, combatTarget);
            else if (defensiveOnlyPet)
                MaintainDefensivePetTree(petBrain, owner, capitalSafety);
            else if (combatTarget?.IsAlive == true && GameServer.ServerRules.IsAllowedToAttack(owner, combatTarget, true))
                CommandPetTree(petBrain, combatTarget);
            else
                FollowPetTree(petBrain, owner, capitalSafety);

            // A subordinate is core Bonedancer combat equipment, not an
            // optional commander buff. Fill or repair the commander's classic
            // slot plan before cycling through pet buffs; otherwise several
            // successful long-duration buffs can postpone the first minion for
            // minutes and a freshly spawned level-19 helper appears broken.
            if (petActionReady && pet is CommanderPet bonedancerCommander)
            {
                PruneBonedancerMinionSlots(bonedancerCommander);
                int desiredCount = DesiredBonedancerMinionCount(bonedancerCommander);
                if (CanAddBonedancerMinion(bonedancerCommander, desiredCount))
                {
                    (Spell Spell, SpellLine Line) minion = ChooseBonedancerMinion(
                        owner,
                        bonedancerCommander,
                        desiredCount,
                        spells.Where(entry => entry.Spell.SpellType == eSpellType.SummonMinion &&
                                              CanCast(owner, entry.Spell)));
                    if (minion.Spell != null && CastPreservingTarget(owner, owner, minion.Spell, minion.Line))
                    {
                        nextDeployablePetTick = now + PetActionCooldown(minion.Spell);
                        activity = $"Summoning Bonedancer minion {minion.Spell.Name}";
                        return true;
                    }
                }
            }

            GameBot petOwnerBot = owner as GameBot;
            eSpecType petOwnerSpec = petOwnerBot?.BotSpec?.SpecType ?? eSpecType.None;
            bool combatOwnsPetTurn = combatTarget?.IsAlive == true ||
                owner.InCombat || owner.IsAttacking || pet.InCombat || pet.IsAttacking ||
                owner is GameBot { Brain: BotBrain cadenceOwnerBrain } && cadenceOwnerBrain.HasAggro;
            int healBelow = combatTarget == null ? 92 : 65;
            if (petActionReady && pet.HealthPercent < healBelow)
            {
                bool covenantHotRunning = characterClass == eCharacterClass.Sluaghbinder &&
                    petOwnerSpec == eSpecType.SluaghbinderCovenant && HasActiveCovenantPetHot(pet);
                (Spell Spell, SpellLine Line) heal = spells
                    .Where(entry => entry.Spell.IsHealing && entry.Spell.Target == eSpellTarget.PET &&
                                    CanCast(owner, entry.Spell) &&
                                    !ShouldSkipActiveCovenantPetHot(
                                        characterClass, petOwnerSpec, entry.Spell, covenantHotRunning))
                    .OrderByDescending(entry => entry.Spell.Level)
                    .FirstOrDefault();
                if (heal.Spell != null && owner.IsWithinRadius(pet, heal.Spell.CalculateEffectiveRange(owner)))
                {
                    if (CastPreservingTarget(owner, pet, heal.Spell, heal.Line))
                    {
                        nextDeployablePetTick = now + PetActionCooldown(heal.Spell);
                        activity = $"Healing pet {pet.Name}";
                        return true;
                    }
                }
            }

            (Spell Spell, SpellLine Line) buff = spells
                .Where(entry => entry.Spell.IsBuff && entry.Spell.Target == eSpellTarget.PET &&
                                !IsCovenantPetHot(characterClass, petOwnerSpec, entry.Spell) &&
                                !CompanionFollowPolicy.DeferBuff(owner, entry.Spell) &&
                                ShouldMaintainRoutinePetBuff(entry.Spell, combatTarget?.IsAlive == true &&
                                    (owner.InCombat || pet.InCombat), owner) &&
                                CanCast(owner, entry.Spell) &&
                                (entry.Spell.SpellType == eSpellType.PetSpell
                                    ? NeedsNecromancerPetCommand(pet, entry.Spell)
                                    : !HasEffect(pet, entry.Spell)))
                .OrderByDescending(entry => entry.Spell.Level)
                .FirstOrDefault();
            if (petActionReady && buff.Spell != null &&
                !IsCovenantPetBuffDeferred(pet, now, combatOwnsPetTurn) &&
                owner.IsWithinRadius(pet, Math.Max(200, buff.Spell.CalculateEffectiveRange(owner))))
            {
                bool combatBlocksServantCast = owner.InCombat || owner.IsAttacking ||
                    pet.InCombat || pet.IsAttacking ||
                    owner is GameBot { Brain: BotBrain ownerBrain } && ownerBrain.HasAggro;
                if (combatBlocksServantCast && IsInterruptibleNecromancerServantBuff(buff.Spell))
                    return false;

                // A PetSpell wrapper is instant on the shade, but its real
                // SubSpellID may be a multi-second cast performed by the
                // servant. Stop both existing movement streams before issuing
                // that command so follow/leash movement cannot cancel it.
                if (IsInterruptibleNecromancerServantBuff(buff.Spell))
                {
                    if (owner is GameBot botOwner && botOwner.IsMoving)
                        botOwner.StopMoving();
                    if (pet.IsMoving)
                        pet.StopMoving();
                }

                if (CastPreservingTarget(owner, pet, buff.Spell, buff.Line))
                {
                    // Pet buffs normally last far longer than the cast itself.
                    // If the effect packet is delayed or the spell is rejected
                    // by a transient state, retrying every pulse starves the
                    // owner's combat/movement brain and leaves Cabalists,
                    // Enchanters and similar pet casters standing in place.
                    // Keep a bounded retry window while allowing normal AI to
                    // run between upkeep attempts.
                    int retryCooldown = PetBuffRetryCooldown(buff.Spell);
                    bool fastCovenantBuff = IsFastRoutinePetBuff(characterClass, petOwnerSpec, buff.Spell);
                    if (fastCovenantBuff)
                    {
                        CovenantPetBuffCadences.GetValue(pet, _ => new CovenantPetBuffCadenceState())
                            .Record(EffectHelper.GetEffectFromSpell(buff.Spell), buff.Spell.ID, now + retryCooldown);
                    }
                    nextDeployablePetTick = now + PetBuffActionCooldown(
                        characterClass, petOwnerSpec, buff.Spell, combatOwnsPetTurn);
                    activity = $"Buffing pet {pet.Name}";
                    return true;
                }
            }

        }

        if (combatTarget?.IsAlive == true && GameLoop.GameLoopTime >= nextDeployablePetTick)
        {
            (Spell Spell, SpellLine Line) theurgistPet = ChooseWeightedByRank(
                spells.Where(entry => entry.Spell.SpellType == eSpellType.SummonTheurgistPet && CanCast(owner, entry.Spell)),
                owner is GameBot { IsEndgameCompanion: true });
            if (theurgistPet.Spell != null && owner.IsWithinRadius(combatTarget, theurgistPet.Spell.CalculateEffectiveRange(owner)))
            {
                if (CastPreservingTarget(owner, combatTarget, theurgistPet.Spell, theurgistPet.Line))
                {
                    // The cast itself is the limiting action. Do not add an AI-only
                    // pause: a Theurgist may immediately begin another legal summon
                    // once the real cast and global timing have completed.
                    nextDeployablePetTick = GameLoop.GameLoopTime + Math.Max(250, theurgistPet.Spell.CastTime + 100);
                    activity = $"Summoning {theurgistPet.Spell.Name} against {combatTarget.Name}";
                    return true;
                }
            }

            (Spell Spell, SpellLine Line) turret = spells
                .Where(entry => IsAnimistFieldTurret(entry.Spell.SpellType) && CanCast(owner, entry.Spell))
                .OrderByDescending(entry => FieldTurretCombatPriority(entry.Spell))
                .ThenByDescending(entry => entry.Spell.Level)
                .FirstOrDefault();
            bool ownerCanCast = turret.Spell != null && spells.Any(entry => entry.Spell.IsHarmful &&
                !AnimistSingleTargetPolicy.IsTurretSummon(entry.Spell) && CanCast(owner, entry.Spell) &&
                owner.IsWithinRadius(combatTarget, entry.Spell.CalculateEffectiveRange(owner)));
            if (turret.Spell != null && !AnimistSingleTargetPolicy.OwnerSpellPending(owner, ownerCanCast) &&
                CanDeployFieldTurret(owner, combatTarget) &&
                owner.IsWithinRadius(combatTarget, turret.Spell.CalculateEffectiveRange(owner)))
            {
                PrepareAnimistGroundTarget(owner, combatTarget, turret.Spell);
                if (CastPreservingTarget(owner, owner, turret.Spell, turret.Line))
                {
                    // In the SI era FnF turrets were constrained chiefly by their
                    // five-second cast, real power cost and two-minute lifetime.
                    // Keep the configured server caps as a safety envelope, but
                    // do not add another AI-only delay between legal casts.
                    nextDeployablePetTick = GameLoop.GameLoopTime + Math.Max(750, turret.Spell.CastTime + 250);
                    activity = $"Planting {turret.Spell.Name}";
                    AnimistSingleTargetPolicy.PlantedTurret(owner, turret.Spell);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Autonomous pets never scan for targets of their own. In capitals they
    /// are additionally passive and disengaged; outside capitals explicit bot
    /// attack orders remain fully functional.
    /// </summary>
    private static bool ApplyCommandOnlyPolicy(GameLiving owner, IControlledBrain petBrain)
    {
        if (owner is not GameBot || petBrain is not ControlledMobBrain controlled)
            return false;

        controlled.AggroLevel = 0;
        bool inCapital = owner.CurrentRegion?.IsCapitalCity == true;
        if (inCapital)
        {
            // Reissuing Passive calls Disengage, which interrupts servant buffs.
            // Keep the real transition/attack cleanup, not an idle self-buff reset.
            if (controlled.AggressionState is not eAggressionState.Passive ||
                !IsNecromancerSelfBuffInProgress(owner, controlled))
            {
                controlled.SetAggressionState(eAggressionState.Passive);
                controlled.Disengage();
            }
        }
        else if (controlled.AggressionState is eAggressionState.Passive)
        {
            controlled.SetAggressionState(eAggressionState.Defensive);
        }
        return inCapital;
    }

    /// <summary>
    /// Sends bounded commands to an existing primary caster pet without
    /// changing the owner's target, movement, casting state, or AI result.
    /// </summary>
    public static void SynchronizeIndependentPet(
        GameLiving owner,
        IControlledBrain petBrain,
        GameLiving combatTarget)
    {
        if (owner is not GameBot { Brain: BotBrain ownerBrain } ||
            petBrain?.Body?.IsAlive != true ||
            petBrain.Body.ObjectState is not GameObject.eObjectState.Active)
            return;

        if (petBrain is ControlledMobBrain controlled)
        {
            controlled.AggroLevel = 0;
            bool inCapital = owner.CurrentRegion?.IsCapitalCity == true;
            if (inCapital)
            {
                if (controlled.AggressionState is not eAggressionState.Passive)
                    controlled.SetAggressionState(eAggressionState.Passive);
                combatTarget = null;
            }
            else if (controlled.AggressionState is eAggressionState.Passive)
            {
                controlled.SetAggressionState(eAggressionState.Defensive);
            }

            if (combatTarget?.IsAlive == true &&
                GameServer.ServerRules.IsAllowedToAttack(owner, combatTarget, true))
            {
                if (controlled.OrderedAttackTarget != combatTarget)
                    controlled.Attack(combatTarget);
                return;
            }

            // Clear only an old combat order after the owner has truly left
            // combat. Native following then resumes without a disengage/follow
            // pair being spammed every lightweight AI pulse.
            if (!owner.InCombat && !owner.IsAttacking && !owner.IsCasting && !ownerBrain.HasAggro &&
                (controlled.OrderedAttackTarget != null || controlled.HasAggro ||
                 controlled.Body.InCombat || controlled.Body.IsAttacking))
                controlled.Disengage();
        }

        if (petBrain.Body.CurrentRegion == owner.CurrentRegion &&
            (petBrain is not ControlledMobBrain current || current.OrderedAttackTarget == null))
            petBrain.Follow(owner);
    }

    private static void CommandPetTree(IControlledBrain petBrain, GameLiving target)
    {
        if (petBrain?.Body == null || target?.IsAlive != true)
            return;

        petBrain.Attack(target);
        if (petBrain.Body.ControlledNpcList == null)
            return;

        foreach (IControlledBrain minionBrain in petBrain.Body.ControlledNpcList)
        {
            if (minionBrain?.Body?.IsAlive != true)
                continue;
            if (minionBrain is ControlledMobBrain minionControlled)
                minionControlled.AggroLevel = 0;
            minionBrain.Attack(target);
        }
    }

    private static void MaintainDefensivePetTree(IControlledBrain petBrain, GameLiving owner, bool forcePassive)
    {
        if (petBrain?.Body == null)
            return;

        if (petBrain is ControlledMobBrain controlled)
        {
            controlled.AggroLevel = 0;
            if (forcePassive)
            {
                controlled.SetAggressionState(eAggressionState.Passive);
                controlled.Disengage();
            }
            else if (controlled.AggressionState is not eAggressionState.Defensive)
            {
                controlled.SetAggressionState(eAggressionState.Defensive);
            }

            bool retaliating = controlled.HasAggro ||
                               controlled.OrderedAttackTarget?.IsAlive == true ||
                               controlled.Body.InCombat || controlled.Body.IsAttacking;
            if (retaliating)
                return;
        }

        FollowPetTree(petBrain, owner, forcePassive);
    }

    private static void FollowPetTree(IControlledBrain petBrain, GameLiving owner, bool forcePassive)
    {
        if (petBrain?.Body == null)
            return;

        petBrain.Follow(owner);
        if (petBrain.Body.ControlledNpcList == null)
            return;

        foreach (IControlledBrain minionBrain in petBrain.Body.ControlledNpcList)
        {
            if (minionBrain?.Body?.IsAlive != true)
                continue;
            if (minionBrain is ControlledMobBrain minionControlled)
            {
                minionControlled.AggroLevel = 0;
                if (forcePassive)
                {
                    minionControlled.SetAggressionState(eAggressionState.Passive);
                    minionControlled.Disengage();
                }
                else if (minionControlled.AggressionState is eAggressionState.Passive)
                {
                    minionControlled.SetAggressionState(eAggressionState.Defensive);
                }
            }
            minionBrain.Follow(petBrain.Body);
        }
    }

    private static void ClearStalePetCombat(
        GameLiving owner,
        IControlledBrain petBrain,
        GameLiving ownerCombatTarget,
        bool defensiveOnlyPet)
    {
        if (defensiveOnlyPet || owner == null || owner.InCombat || owner.IsAttacking || owner.IsCasting ||
            ownerCombatTarget?.IsAlive == true || petBrain is not ControlledMobBrain controlled ||
            controlled.Body?.IsAlive != true)
            return;

        if (IsNecromancerSelfBuffInProgress(owner, controlled))
            return;

        GameLiving petTarget = controlled.Body.TargetObject as GameLiving;
        // Follow must supersede an old explicit attack order after the owner has
        // left combat. ControlledMobBrain.Follow does not clear that order, so a
        // still-living former target can otherwise pull the pet back into AGGRO
        // forever and strand both autonomous and temporary caster-pet bots.
        if (owner is GameBot { Brain: BotBrain ownerBrain } && !ownerBrain.HasAggro &&
            (petTarget?.IsAlive == true && petTarget != controlled.Body || controlled.HasAggro ||
             controlled.Body.IsAttacking || controlled.Body.InCombat))
        {
            controlled.Disengage();
            return;
        }

        if ((controlled.HasAggro || controlled.Body.IsAttacking || controlled.Body.InCombat) &&
            petTarget?.IsAlive != true)
            controlled.Disengage();
    }

    public static bool IsNecromancerSelfBuffInProgress(GameLiving owner, ControlledMobBrain brain)
    {
        // Do not infer safety from clean combat flags: the exact defect is a
        // stale flag surviving into a real self-buff. Require the native
        // Necromancer queue/casting pipeline to prove a helpful self-command.
        return owner is GameBot && brain is NecromancerPetBrain necromancer &&
               necromancer.HasPendingSelfBuffCommand;
    }

    /// <summary>
    /// Gives a normal controlled pet first contact on most fresh pulls. The
    /// owner only pauses briefly, and never pauses when already under attack.
    /// Target state is supplied by the caller so this remains allocation-free
    /// for hundreds of autonomous characters.
    /// </summary>
    public static bool LetMainPetLead(
        GameLiving owner,
        GameLiving target,
        ref GameLiving leadTarget,
        ref long ownerHoldUntil)
    {
        IControlledBrain petBrain = owner?.ControlledBrain;
        GameNPC pet = petBrain?.Body;
        if (target?.IsAlive != true || pet?.IsAlive != true ||
            pet.ObjectState is not GameObject.eObjectState.Active ||
            !GameServer.ServerRules.IsAllowedToAttack(owner, target, true))
        {
            leadTarget = null;
            ownerHoldUntil = 0;
            return false;
        }

        if (leadTarget != target)
        {
            leadTarget = target;
            ownerHoldUntil = GameLoop.GameLoopTime;
            petBrain.Attack(target);

            // Pet-first means the pet receives the first order/pulse. It must
            // never serialize the owner's combat rotation behind pet travel.
            // On the following brain pulse the owner acts independently.
            if (target.TargetObject != owner && !owner.InCombat && !owner.IsAttacking)
            {
                if (owner is GamePlayer player)
                    player.attackComponent.StopAttack();
                else if (owner is GameNPC npc)
                    npc.StopAttack();
                return true;
            }
        }

        ownerHoldUntil = 0;
        return false;
    }

    public static bool IsDefensiveOnlyPetClass(eCharacterClass characterClass, bool groupedBot = false) =>
        characterClass == eCharacterClass.Druid || characterClass == eCharacterClass.Minstrel && !groupedBot;

    /// <summary>
    /// Five-to-thirty second pet shields and procs are combat preparations, not
    /// permanent travel buffs. At high population the AI cadence can be longer
    /// than those effects, so treating them as routine upkeep makes a pet class
    /// stop and recast on every turn forever (notably level-one Enchanters with
    /// Aura of Echoing). Long-duration stat buffs remain normal upkeep; short
    /// tactical effects are refreshed only when a live combat target exists.
    /// </summary>
    public static bool ShouldMaintainRoutinePetBuff(Spell spell, bool hasCombatTarget, GameLiving owner = null)
    {
        if (spell == null)
            return false;
        // These bots fight alongside their pet. A focus shield is a continuous
        // channel, not a fire-and-forget buff, and consumes every casting turn
        // while the pet fights. Leave it available to manual/player-hotbar
        // casts, but never select it as their autonomous pet upkeep.
        if (IsDisabledBotDamageShield(owner, spell))
            return false;

        // Spell.Duration is normalized to milliseconds by Spell's constructor.
        bool shortTacticalEffect = spell.Concentration <= 0 && spell.Duration > 0 && spell.Duration <= 30_000;
        return !shortTacticalEffect || hasCombatTarget;
    }

    public static bool IsDisabledBotDamageShield(GameLiving owner, Spell spell) =>
        spell?.SpellType == eSpellType.DamageShield && owner is GameBot bot &&
        (eCharacterClass?)bot.CharacterClass?.ID is eCharacterClass.Cabalist or eCharacterClass.Enchanter or
            eCharacterClass.Spiritmaster;

    /// <summary>
    /// The three primary caster-pet classes must be allowed to acquire and
    /// follow a world objective before pet upkeep can consume their entire
    /// think budget.  Pet maintenance becomes authoritative again at the camp
    /// and in combat.  Temporary companions and player-led bots are excluded.
    /// </summary>
    public static bool ShouldPrioritizeWorldMovement(
        eCharacterClass characterClass,
        bool persistentWorldBot,
        bool temporaryHelper,
        bool playerLed,
        bool inCombat,
        bool isMoving,
        bool hasCamp,
        string activity)
    {
        if (!persistentWorldBot || temporaryHelper || playerLed || inCombat ||
            characterClass is not (eCharacterClass.Bonedancer or eCharacterClass.Enchanter or eCharacterClass.Cabalist or
                                    eCharacterClass.Spiritmaster or eCharacterClass.Sorcerer or
                                    eCharacterClass.Mentalist or eCharacterClass.Minstrel or
                                    eCharacterClass.Sluaghbinder))
        {
            return false;
        }

        if (!hasCamp || isMoving)
            return true;

        string current = activity ?? string.Empty;
        return current.Contains("choosing", StringComparison.OrdinalIgnoreCase) ||
               current.Contains("searching", StringComparison.OrdinalIgnoreCase) ||
               current.Contains("planning", StringComparison.OrdinalIgnoreCase) ||
               current.Contains("travel", StringComparison.OrdinalIgnoreCase) ||
               current.Contains("walking", StringComparison.OrdinalIgnoreCase) ||
               current.Contains("crossing", StringComparison.OrdinalIgnoreCase) ||
               current.Contains("meeting", StringComparison.OrdinalIgnoreCase) ||
               current.Contains("returning", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A primary combat pet is class equipment, not optional upkeep. Region
    /// changes remove ordinary controlled NPCs, so persistent pet casters must
    /// get one legal summon attempt before continuing a dungeon route. Failed
    /// casts return false from Maintain and therefore never starve movement.
    /// </summary>
    public static bool NeedsPrimaryPetUpkeep(GameBot owner)
    {
        if (owner?.CharacterClass == null || owner.ControlledBrain?.Body?.IsAlive == true)
            return false;

        return (eCharacterClass)owner.CharacterClass.ID is
            eCharacterClass.Bonedancer or eCharacterClass.Enchanter or eCharacterClass.Cabalist or
            eCharacterClass.Spiritmaster or eCharacterClass.Sorcerer or eCharacterClass.Mentalist or
            eCharacterClass.Necromancer or eCharacterClass.Hunter or eCharacterClass.Druid or
            eCharacterClass.Minstrel or eCharacterClass.Sluaghbinder;
    }

    /// <summary>
    /// A learned Bonedancer subordinate is core combat equipment. Persistent
    /// travel may defer optional buffs, but it may not defer an empty commander
    /// slot forever. This check is read-only; Maintain performs the actual
    /// pruning and legal cast through the native summon handler.
    /// </summary>
    public static bool NeedsBonedancerArmyUpkeep(GameBot owner)
    {
        if (owner?.CharacterClass?.ID != (int)eCharacterClass.Bonedancer ||
            owner.ControlledBrain?.Body is not CommanderPet commander ||
            commander.SubPetCapacity <= 0)
        {
            return false;
        }

        int desiredCount = DesiredBonedancerMinionCount(commander);
        int activeCount;
        lock (commander.ControlledNpcListLock)
        {
            activeCount = commander.ControlledNpcList?.Count(brain => brain?.Body is
            {
                IsAlive: true,
                ObjectState: GameObject.eObjectState.Active
            }) ?? 0;
        }

        bool hasKnownMinion = GetBonedancerMinionSpells(owner)
            .Any(entry => entry.Spell?.Level <= owner.Level);
        return IsRequiredBonedancerArmyGap(desiredCount, activeCount, hasKnownMinion);
    }

    /// <summary>
    /// Temporary player companions follow a moving owner almost continuously,
    /// so their ordinary optional-maintenance window may never open. Keep the
    /// Bonedancer commander and its learned subordinate as required companion
    /// equipment without changing persistent autonomous gamebot scheduling.
    /// </summary>
    public static bool NeedsTemporaryCompanionBonedancerArmyUpkeep(GameBot owner)
    {
        if (!IsTemporaryCompanionBonedancerScope(
                owner?.IsTemporaryGroupHelper == true,
                owner?.IsAutonomousWorldBot == true,
                owner?.CharacterClass?.ID ?? 0) ||
            !owner.IsAlive || owner.InCombat || owner.IsAttacking || owner.IsCasting)
        {
            return false;
        }

        IControlledBrain petBrain = owner.ControlledBrain;
        if (petBrain?.Body?.IsAlive != true ||
            petBrain.Body.ObjectState is not GameObject.eObjectState.Active)
        {
            return true;
        }

        if (petBrain.Body is not CommanderPet commander)
            return false;

        return ShouldUpgradeBonedancerCommander(owner, commander, BotKnownSpells(owner), null) ||
               NeedsBonedancerArmyUpkeep(owner);
    }

    public static bool IsTemporaryCompanionBonedancerScope(
        bool temporaryHelper,
        bool autonomousWorldBot,
        int characterClassId) =>
        temporaryHelper && !autonomousWorldBot &&
        characterClassId == (int)eCharacterClass.Bonedancer;

    public static bool IsRequiredBonedancerArmyGap(
        int desiredCount,
        int activeCount,
        bool hasKnownMinion) =>
        desiredCount > 0 && activeCount < desiredCount && hasKnownMinion;

    public static bool IsMainPetSummon(eSpellType type) => type is
        eSpellType.SummonCommander or
        eSpellType.SummonUnderhill or
        eSpellType.SummonDruidPet or
        eSpellType.SummonSimulacrum or
        eSpellType.SummonSpiritFighter or
        eSpellType.SummonHunterPet or
        eSpellType.SummonAnimistPet or
        eSpellType.SummonNecroPet;

    public static bool IsAnimistFieldTurret(eSpellType type) => type is
        eSpellType.SummonAnimistFnF or
        eSpellType.SummonAnimistFnFCustom or
        eSpellType.SummonAnimistAmbusher;

    private static int FieldTurretCombatPriority(Spell summon)
    {
        Spell payload = summon?.SubSpellID > 0 ? SkillBase.GetSpellByID(summon.SubSpellID) : null;
        if (payload?.IsHarmful == true && payload.Damage > 0)
            return 3;
        if (payload?.IsHarmful == true)
            return 2;
        return 1;
    }

    internal static bool CanDeployFieldTurret(GameLiving owner, GameLiving combatTarget) =>
        owner != null && CanDeployFieldTurretAt(owner, new Point3D(
            combatTarget?.X ?? owner.X,
            combatTarget?.Y ?? owner.Y,
            combatTarget?.Z ?? owner.Z));

    /// <summary>The server's own turret caps (per owner, and per area around the ground target).</summary>
    internal static bool CanDeployFieldTurretAt(GameLiving owner, Point3D center)
    {
        if (owner == null)
            return false;

        int ownedCount = FieldTurrets.TryGetValue(owner, out var owned)
            ? owned.Count(entry => entry.Key?.ObjectState is GameObject.eObjectState.Active)
            : 0;
        if (Properties.TURRET_PLAYER_CAP_COUNT > 0 && ownedCount >= Properties.TURRET_PLAYER_CAP_COUNT)
            return false;

        if (Properties.TURRET_AREA_CAP_COUNT <= 0 || owner.CurrentRegion == null)
            return true;

        int nearby = owner.CurrentRegion.GetNPCsInRadius(center, (ushort)Properties.TURRET_AREA_CAP_RADIUS)
            .Count(npc => npc?.Brain is TurretFNFBrain);
        return nearby < Properties.TURRET_AREA_CAP_COUNT;
    }

    public static void CancelPendingCharm(GameLiving owner)
    {
        if (owner != null && PendingCharms.TryRemove(owner, out PendingCharm pending))
            DeleteSyntheticCharm(pending.Mob);
        if (owner != null && ActiveSyntheticCharms.TryRemove(owner, out ActiveSyntheticCharm active) &&
            owner.ControlledBrain?.Body != active.Mob)
        {
            // A released charm has no controlled brain. Deleting the tracking
            // entry alone used to strand its synthetic body in the world.
            DeleteSyntheticCharm(active.Mob);
        }
    }

    private static void WatchGameBotSyntheticCharm(GameBot owner, GameNPC mob)
    {
        long unboundSinceTick = 0;
        _ = new ECSGameTimer(mob, _ =>
        {
            if (mob.ObjectState is not GameObject.eObjectState.Active || !mob.IsAlive)
            {
                ForgetSyntheticCharm(owner, mob);
                return 0;
            }

            if (owner.ObjectState is not GameObject.eObjectState.Active || !owner.IsAlive ||
                owner.CurrentRegion != mob.CurrentRegion)
            {
                ForgetSyntheticCharm(owner, mob);
                DeleteSyntheticCharm(mob);
                return 0;
            }

            if (owner.ControlledBrain?.Body == mob)
            {
                unboundSinceTick = 0;
                return 5_000;
            }

            long now = GameLoop.GameLoopTime;
            if (PendingCharms.TryGetValue(owner, out PendingCharm pending) && pending.Mob == mob &&
                (now < pending.ExpiresAtTick || owner.IsCasting))
            {
                // The normal charm cast still owns this candidate. Do not
                // remove a successful cast just because its effect is pending.
                unboundSinceTick = 0;
                return 1_000;
            }

            unboundSinceTick = unboundSinceTick == 0 ? now : unboundSinceTick;
            if (!IsExpiredSyntheticCharmBinding(now, unboundSinceTick))
                return 1_000;

            ForgetSyntheticCharm(owner, mob);
            DeleteSyntheticCharm(mob);
            return 0;
        }, 3_000);
    }

    private static void ForgetSyntheticCharm(GameLiving owner, GameNPC mob)
    {
        if (PendingCharms.TryGetValue(owner, out PendingCharm pending) && pending.Mob == mob)
            ((ICollection<KeyValuePair<GameLiving, PendingCharm>>)PendingCharms)
                .Remove(new KeyValuePair<GameLiving, PendingCharm>(owner, pending));
        if (ActiveSyntheticCharms.TryGetValue(owner, out ActiveSyntheticCharm active) && active.Mob == mob)
            ((ICollection<KeyValuePair<GameLiving, ActiveSyntheticCharm>>)ActiveSyntheticCharms)
                .Remove(new KeyValuePair<GameLiving, ActiveSyntheticCharm>(owner, active));
    }

    private static bool ReconcileActiveSyntheticCharm(GameLiving owner, long now,
        ref long nextDeployablePetTick, out string activity)
    {
        activity = string.Empty;
        if (!ActiveSyntheticCharms.TryGetValue(owner, out ActiveSyntheticCharm active))
            return false;
        GameNPC controlled = owner.ControlledBrain?.Body;
        bool invalid = active.Mob == null || !active.Mob.IsAlive ||
                       active.Mob.ObjectState is not GameObject.eObjectState.Active ||
                       active.Mob.CurrentRegion != owner.CurrentRegion;
        if (!invalid && controlled == active.Mob)
        {
            active.MissingBrainSinceTick = 0;
            return false;
        }
        if (!invalid && controlled == null)
        {
            active.MissingBrainSinceTick = active.MissingBrainSinceTick == 0 ? now : active.MissingBrainSinceTick;
            if (now - active.MissingBrainSinceTick < 2_000)
            {
                activity = $"Reacquiring {active.Mob.Name}";
                return true;
            }
        }
        ActiveSyntheticCharms.TryRemove(owner, out _);
        DeleteSyntheticCharm(active.Mob);
        nextDeployablePetTick = Math.Max(nextDeployablePetTick, now + 3_000);
        return false;
    }

    public static void RegisterFieldTurret(GameLiving owner, TurretPet turret, int lifetimeMilliseconds = 120_000)
    {
        if (owner == null || turret == null)
            return;
        FieldTurrets.GetOrAdd(owner, _ => new ConcurrentDictionary<TurretPet, long>())[turret] =
            GameLoop.GameLoopTime + Math.Max(1_000, lifetimeMilliseconds);
    }

    /// <summary>This owner's live field turrets and when each one expires.</summary>
    public static IEnumerable<(TurretPet Turret, long ExpiresAt)> OwnedFieldTurrets(GameLiving owner)
    {
        if (owner == null || !FieldTurrets.TryGetValue(owner, out var turrets))
            yield break;
        foreach (KeyValuePair<TurretPet, long> entry in turrets)
            if (entry.Key?.IsAlive == true && entry.Key.ObjectState is GameObject.eObjectState.Active)
                yield return (entry.Key, entry.Value);
    }

    public static void UnregisterFieldTurret(GameLiving owner, TurretPet turret)
    {
        if (owner == null || turret == null || !FieldTurrets.TryGetValue(owner, out var turrets))
            return;
        turrets.TryRemove(turret, out _);
        if (turrets.IsEmpty)
            FieldTurrets.TryRemove(owner, out _);
    }

    public static void ReleaseFieldTurrets(GameLiving owner)
    {
        if (owner == null || !FieldTurrets.TryRemove(owner, out var turrets))
            return;
        foreach (TurretPet turret in turrets.Keys)
        {
            if (turret?.ObjectState is GameObject.eObjectState.Active)
                turret.Delete();
        }
    }

    private static void PruneDistantFieldTurrets(GameLiving owner)
    {
        if (owner == null || !FieldTurrets.TryGetValue(owner, out var turrets))
            return;
        // Lock-free enumeration (.Keys takes every bucket lock each AI turn).
        foreach (TurretPet turret in turrets.Select(entry => entry.Key))
        {
            bool stale = turret == null || turret.ObjectState is not GameObject.eObjectState.Active ||
                         turret.CurrentRegion != owner.CurrentRegion || !owner.IsWithinRadius(turret, 3200);
            if (!stale)
                continue;
            turrets.TryRemove(turret, out _);
            if (turret?.ObjectState is GameObject.eObjectState.Active)
                turret.Delete();
        }
        if (turrets.IsEmpty)
            FieldTurrets.TryRemove(owner, out _);
    }

    public static bool IsCharmBodyTypeAllowed(Spell spell, ushort bodyType)
    {
        return spell != null && IsCharmBodyTypeAllowed((CharmSpellHandler.eCharmType)spell.AmnesiaChance, bodyType);
    }

    public static bool IsCharmBodyTypeAllowed(CharmSpellHandler.eCharmType charmType, ushort bodyType)
    {
        if (bodyType is < 1 or > 11)
            return false;
        return charmType switch
        {
            CharmSpellHandler.eCharmType.All => true,
            CharmSpellHandler.eCharmType.Humanoid => bodyType == (ushort)NpcTemplateMgr.eBodyType.Humanoid,
            CharmSpellHandler.eCharmType.Animal => bodyType == (ushort)NpcTemplateMgr.eBodyType.Animal,
            CharmSpellHandler.eCharmType.Insect => bodyType == (ushort)NpcTemplateMgr.eBodyType.Insect,
            CharmSpellHandler.eCharmType.Reptile => bodyType == (ushort)NpcTemplateMgr.eBodyType.Reptile,
            CharmSpellHandler.eCharmType.HumanoidAnimal => bodyType is
                (ushort)NpcTemplateMgr.eBodyType.Humanoid or (ushort)NpcTemplateMgr.eBodyType.Animal,
            CharmSpellHandler.eCharmType.HumanoidAnimalInsect => bodyType is
                (ushort)NpcTemplateMgr.eBodyType.Humanoid or (ushort)NpcTemplateMgr.eBodyType.Animal or
                (ushort)NpcTemplateMgr.eBodyType.Insect or (ushort)NpcTemplateMgr.eBodyType.Reptile,
            CharmSpellHandler.eCharmType.HumanoidAnimalInsectMagical => bodyType is
                (ushort)NpcTemplateMgr.eBodyType.Humanoid or (ushort)NpcTemplateMgr.eBodyType.Animal or
                (ushort)NpcTemplateMgr.eBodyType.Insect or (ushort)NpcTemplateMgr.eBodyType.Reptile or
                (ushort)NpcTemplateMgr.eBodyType.Magical or (ushort)NpcTemplateMgr.eBodyType.Plant or
                (ushort)NpcTemplateMgr.eBodyType.Elemental,
            CharmSpellHandler.eCharmType.HumanoidAnimalInsectMagicalUndead => bodyType is
                (ushort)NpcTemplateMgr.eBodyType.Humanoid or (ushort)NpcTemplateMgr.eBodyType.Animal or
                (ushort)NpcTemplateMgr.eBodyType.Insect or (ushort)NpcTemplateMgr.eBodyType.Reptile or
                (ushort)NpcTemplateMgr.eBodyType.Magical or (ushort)NpcTemplateMgr.eBodyType.Plant or
                (ushort)NpcTemplateMgr.eBodyType.Elemental or (ushort)NpcTemplateMgr.eBodyType.Undead,
            _ => false
        };
    }

    private static Spell CreateGeneratedCharmSpell(GeneratedCharmProfile profile)
    {
        DbSpell dbSpell = new()
        {
            SpellID = 0,
            ClientEffect = profile.ClientEffect,
            Icon = profile.ClientEffect,
            Name = profile.SpellName,
            Description = "Maintains one permanent generated companion for this GameBot.",
            Target = "Enemy",
            Range = 2_000,
            Power = 0,
            CastTime = profile.CastTimeMilliseconds / 1000d,
            Damage = 100,
            Type = eSpellType.Charm.ToString(),
            Duration = ushort.MaxValue,
            Pulse = 0,
            Frequency = 0,
            Value = 50,
            AmnesiaChance = (int)CharmSpellHandler.eCharmType.All,
            Message2 = "{0} is now under your control.",
            Message4 = "You lose control of {0}."
        };
        return new Spell(dbSpell, profile.UnlockLevel);
    }

    public static bool IsGeneratedCharmTemplateRegion(eCharacterClass characterClass, ushort region) =>
        (region == DarknessFallsCharmPolicy.RegionId && characterClass is
            eCharacterClass.Sorcerer or eCharacterClass.Mentalist or eCharacterClass.Minstrel or eCharacterClass.Hunter) ||
        (characterClass switch
        {
            eCharacterClass.Sorcerer or eCharacterClass.Minstrel => region is 1 or 10 or 20 or 21 or 22 or 23 or 24 or 50 or 51 or 60 or 61 or 62,
            eCharacterClass.Mentalist => region is 180 or 181 or 190 or 191 or 192 or 193 or 194 or 200 or 201 or 220 or 221 or 222 or 223 or 224,
            eCharacterClass.Hunter => region is 100 or 101 or 102 or 125 or 126 or 127 or 128 or 129 or 150 or 151 or 160 or 161,
            _ => false
        });

    public static DbMob[] GetPlayerCharmChoices(GamePlayer owner, Spell spell)
    {
        if (owner == null || spell?.SpellType != eSpellType.Charm)
            return Array.Empty<DbMob>();
        eCharacterClass characterClass = (eCharacterClass)owner.CharacterClass.ID;
        int level = PlayerGeneratedCharmPolicy.TargetLevel(characterClass, spell.ID, owner.Level);
        if (level == 0) return Array.Empty<DbMob>();
        return PlayerCharmTemplates.GetOrAdd((level, (ushort)spell.AmnesiaChance, characterClass), key =>
            PlayerGeneratedCharmPolicy.SelectTemplates(
                Enumerable.Range(Math.Max(1, key.Level - 2), key.Level + 3 - Math.Max(1, key.Level - 2))
                    .SelectMany(candidateLevel => DOLDB<DbMob>.SelectObjects(DB.Column("Level").IsEqualTo(candidateLevel)
                        .And(DB.Column("Realm").IsEqualTo(0))))
                    .Where(mob => mob != null && mob.ClassType == DbMob.DEFAULT_NPC_CLASSTYPE &&
                        IsGeneratedCharmTemplateRegion(key.Class, mob.Region) &&
                        !CharmCreatureRoles.IsImmobile(mob) &&
                        !string.IsNullOrWhiteSpace(mob.Name) &&
                        (Properties.SPELL_CHARM_NAMED_CHECK == 0 || char.IsLower(mob.Name[0])) &&
                        (mob.Region == DarknessFallsCharmPolicy.RegionId
                            ? DarknessFallsCharmPolicy.AllowsPlayerChoice(mob, spell, key.Class)
                            : IsCharmBodyTypeAllowed(spell, (ushort)mob.BodyType))), key.Level));
    }

    private static bool TryCreateCharmCandidate(GameLiving owner, Spell spell,
        GeneratedCharmProfile profile, eCharacterClass characterClass, out GameNPC candidate,
        bool placeInFront = false, DbMob selectedTemplate = null)
    {
        candidate = null;
        if (owner.CurrentRegion == null || owner.CurrentZone == null || owner.Level < profile.UnlockLevel)
            return false;

        int targetLevel = Math.Max(1, owner.Level - profile.PetLevelOffset);
        ushort charmType = (ushort)spell.AmnesiaChance;
        DbMob[] templates = CharmTemplates.GetOrAdd((targetLevel, charmType, characterClass), key =>
            DOLDB<DbMob>.SelectObjects(DB.Column("Level").IsEqualTo(key.Level)
                    .And(DB.Column("Realm").IsEqualTo(0)))
                .Where(mob => mob != null && mob.ClassType == DbMob.DEFAULT_NPC_CLASSTYPE &&
                              IsGeneratedCharmTemplateRegion(key.Class, mob.Region) &&
                              // Speed-0 creatures (clinging soul, lookouts) could never follow.
                              !CharmCreatureRoles.IsImmobile(mob) &&
                              !string.IsNullOrWhiteSpace(mob.Name) &&
                              (Properties.SPELL_CHARM_NAMED_CHECK == 0 || char.IsLower(mob.Name[0])) &&
                              (mob.Region == DarknessFallsCharmPolicy.RegionId
                                  ? DarknessFallsCharmPolicy.AllowsBotChoice(mob, spell, key.Class, owner.Level)
                                  : IsCharmBodyTypeAllowed(spell, (ushort)mob.BodyType)))
                .ToArray());
        if (owner is GamePlayer player && placeInFront)
        {
            templates = GetPlayerCharmChoices(player, spell);
            if (selectedTemplate == null || !templates.Contains(selectedTemplate))
                return false;
        }
        if (templates.Length == 0)
            return false;

        DbMob template = selectedTemplate ?? templates[Random.Shared.Next(templates.Length)];
        GameNPC mob = new();
        mob.LoadFromDatabase(template);
        // This body is a temporary charm candidate, not a world spawn.
        // Zero means "use the default respawn interval" in GameNPC, so
        // only a negative value prevents an uncharmed, killed candidate
        // from repeatedly respawning at the bot's former location.
        if (owner is GameBot)
            mob.RespawnInterval = SyntheticCharmRespawnInterval(mob.RespawnInterval, true);
        if (template.Region == DarknessFallsCharmPolicy.RegionId &&
            DarknessFallsCharmPolicy.TryGetCharmBodyType(template, out ushort dfBodyType))
        {
            // The synthetic pet must retain the exact selected familiar form,
            // even if an older multi-model NPC template is still installed.
            mob.Model = template.Model;
            mob.BodyType = dfBodyType;
        }
        mob.InternalID = null;
        mob.LoadedFromScript = true;
        mob.CurrentRegion = owner.CurrentRegion;
        Vector3 origin = new(owner.X, owner.Y, owner.Z);
        Vector3 location;
        if (placeInFront)
        {
            Point2D front = owner.GetPointFromHeading(owner.Heading, 64);
            Vector3 desired = new(front.X, front.Y, owner.Z);
            location = PathfindingProvider.Instance.GetClosestPoint(owner.CurrentZone, desired,
                           32, 32, 96, PathfindingProvider.Instance.DefaultFilters) ?? desired;
        }
        else
        {
            location = PathfindingProvider.Instance.GetRandomPoint(
                owner.CurrentZone, origin, 90, PathfindingProvider.Instance.DefaultFilters) ??
                       new Vector3(owner.X + 35, owner.Y + 35, owner.Z);
        }
        mob.X = (int)location.X;
        mob.Y = (int)location.Y;
        mob.Z = (int)location.Z;
        mob.Realm = eRealm.None;
        mob.Level = (byte)targetLevel;
        mob.TempProperties.SetProperty(SyntheticCharmPetProperty, true);
        if (mob.Brain is IOldAggressiveBrain aggressive)
        {
            aggressive.AggroLevel = 0;
            aggressive.AggroRange = 0;
            aggressive.ClearAggroList();
        }
        if (!mob.AddToWorld())
        {
            mob.Delete();
            return false;
        }
        if (owner is GameBot gameBot)
            WatchGameBotSyntheticCharm(gameBot, mob);
        candidate = mob;
        return true;
    }

    private static void DeleteSyntheticCharm(GameNPC mob)
    {
        if (mob?.TempProperties.GetProperty<bool>(SyntheticCharmPetProperty) != true)
            return;
        if (mob.ObjectState is GameObject.eObjectState.Active)
            mob.RemoveFromWorld();
        mob.Delete();
    }

    internal static IEnumerable<(Spell Spell, SpellLine Line)> KnownSpells(GameLiving owner)
    {
        if (owner is GamePlayer player)
        {
            foreach ((SpellLine line, List<Skill> skills) in player.GetAllUsableListSpells())
            foreach (Spell spell in skills.OfType<Spell>())
                yield return (spell, line);
            yield break;
        }

        if (owner is GameBot cachedBot)
        {
            foreach (var entry in BotKnownSpells(cachedBot))
                yield return entry;
        }
    }

    // A bot's spell list only changes when it trains or levels; pet upkeep read it (with power-line
    // resolution) on every AI turn of every pet class (115 MB of allocations in 40 s, run 8 trace).
    private sealed class KnownSpellCache
    {
        public object Source; public int Count; public byte Level; public long Until;
        public (Spell Spell, SpellLine Line)[] Items = [];
        public (Spell Spell, SpellLine Line)[] Automated = [];
    }
    private static readonly ConditionalWeakTable<GameBot, KnownSpellCache> KnownSpellCaches = new();

    private static (Spell Spell, SpellLine Line)[] BotKnownSpells(GameBot bot)
    {
        KnownSpellCache cache = KnownSpellCaches.GetOrCreateValue(bot);
        long now = GameLoop.GameLoopTime;
        var source = bot.Spells;
        lock (cache)
        {
            if (now < cache.Until && ReferenceEquals(cache.Source, source) && cache.Count == (source?.Count ?? 0) && cache.Level == bot.Level)
                return cache.Items;
            cache.Items = UncachedBotKnownSpells(bot).ToArray();
            cache.Automated = Array.FindAll(cache.Items, entry => AnimistSingleTargetPolicy.AllowsAutomatedSpell(entry.Spell));
            cache.Source = source; cache.Count = source?.Count ?? 0; cache.Level = bot.Level;
            cache.Until = now + 10_000;
            return cache.Items;
        }
    }

    // The upkeep list without a per-call filter (every ordinary pet turn), cached with the known spells.
    private static (Spell Spell, SpellLine Line)[] BotAutomatedSpells(GameBot bot)
    {
        BotKnownSpells(bot);
        KnownSpellCache cache = KnownSpellCaches.GetOrCreateValue(bot);
        lock (cache) return cache.Automated;
    }

    private static IEnumerable<(Spell Spell, SpellLine Line)> UncachedBotKnownSpells(GameBot bot)
    {
        {
            bool bonedancer = bot.CharacterClass?.ID == (int)eCharacterClass.Bonedancer;
            foreach (Spell spell in bot.Spells?
                .Where(spell => spell != null && !SluaghbinderBotPolicy.IsPlayerOnlyServiceSpell(spell)) ??
                Enumerable.Empty<Spell>())
                yield return (spell, bot.ResolvePowerSpellLine(spell, MobSpellLine));

            // SetCasterSpells intentionally retains only the highest rank per
            // role. Bonedancers are the exception: a three-pet level-45 plan
            // needs lower ranks to remain under the shared level-75 budget.
            // Cache the complete learned subordinate catalog by level/spec
            // fingerprint so the hot AI path remains cheap at high population.
            if (bonedancer)
                foreach (var minion in GetBonedancerMinionSpells(bot))
                    yield return minion;
        }
    }

    private static IReadOnlyList<(Spell Spell, SpellLine Line)> GetBonedancerMinionSpells(GameBot bot)
    {
        int fingerprint = HashCode.Combine(
            bot.Level,
            bot.GetBaseSpecLevel(Specs.Darkness),
            bot.GetBaseSpecLevel(Specs.Suppression),
            bot.GetBaseSpecLevel(Specs.BoneArmy));
        BonedancerMinionSpellCache cache = BonedancerMinionSpells.GetOrCreateValue(bot);
        if (cache.Initialized && cache.Fingerprint == fingerprint)
            return cache.Spells;

        List<Tuple<SpellLine, List<Skill>>> usable = bot.GetAllUsableListSpells(true);
        cache.Spells = usable
            .Where(entry => entry?.Item1 != null && entry.Item2 != null)
            .SelectMany(entry => entry.Item2.OfType<Spell>()
                .Where(spell => spell.SpellType == eSpellType.SummonMinion && spell.Level <= bot.Level)
                .Select(spell => (Spell: spell, Line: entry.Item1)))
            .DistinctBy(entry => entry.Spell.ID)
            .ToList();
        cache.Fingerprint = fingerprint;
        cache.Initialized = true;
        bot.TempProperties.SetProperty(BonedancerSpellRefreshProperty, (int)bot.Level);
        return cache.Spells;
    }

    internal static bool CanCast(GameLiving owner, Spell spell)
    {
        if (spell == null || spell.Level > owner.Level ||
            spell.HasRecastDelay && owner.GetSkillDisabledDuration(spell) > 0)
            return false;

        int powerCost = owner switch
        {
            GameBot bot => bot.PowerCost(spell),
            _ => 0,
        };
        return owner.Mana >= powerCost;
    }

    private static void EnsureStanding(GameLiving owner)
    {
        if (owner is GameBot bot)
        {
            if (bot.IsRecoveryResting)
                bot.WakeRecoveryRest();
            return;
        }

        if (!owner.IsSitting)
            return;

        if (owner is GamePlayer player)
            player.Sit(false);
    }

    /// <summary>
    /// Pet upkeep temporarily selects the pet (or the owner for a summon) as
    /// the spell target. Preserve the owner's live combat target so the next
    /// bot pulse can continue its own damage rotation instead of losing the
    /// fight after the pet's first order.
    /// </summary>
    private static bool CastPreservingTarget(GameLiving owner, GameLiving target, Spell spell, SpellLine line)
    {
        if (owner == null || target == null || spell == null)
            return false;

        GameObject previousTarget = owner.TargetObject;
        owner.TargetObject = target;
        EnsureStanding(owner);
        bool cast = owner.CastSpell(spell, line);
        owner.TargetObject = previousTarget;
        return cast;
    }

    private static int PetActionCooldown(Spell spell) =>
        Math.Max(1_500, (spell?.CastTime ?? 0) + 500);

    private static int PetBuffRetryCooldown(Spell spell) =>
        Math.Max(15_000, (spell?.CastTime ?? 0) + 1_500);

    /// <summary>
    /// Keeps pet choice dynamic without making weak pets commonplace. Rank zero
    /// receives roughly 78% of the probability mass; every lower rank receives
    /// 22% of the previous rank's weight.
    /// </summary>
    // Reused per thread: pet upkeep asks every turn and usually finds no summon at all
    // (the old GroupBy/OrderBy version was ~33 MB per 40 s of allocations in run 11).
    [ThreadStatic] private static List<(Spell Spell, SpellLine Line)> _rankPool;

    public static (Spell Spell, SpellLine Line) ChooseWeightedByRank(
        IEnumerable<(Spell Spell, SpellLine Line)> available, bool highestRankOnly = false)
    {
        List<(Spell Spell, SpellLine Line)> pool = _rankPool ??= new();
        pool.Clear();
        foreach (var entry in available)
            if (entry.Spell != null) pool.Add(entry);
        if (pool.Count == 0)
            return default;

        if (highestRankOnly)
        {
            var best = pool[0];
            for (int i = 1; i < pool.Count; i++)
                if (pool[i].Spell.Level > best.Spell.Level ||
                    pool[i].Spell.Level == best.Spell.Level && pool[i].Spell.ID > best.Spell.ID)
                    best = pool[i];
            return best;
        }

        // Ranks (distinct spell levels), highest first; each lower rank weighs 0.22 of the one above.
        pool.Sort(static (a, b) => b.Spell.Level.CompareTo(a.Spell.Level));
        int ranks = 1;
        for (int i = 1; i < pool.Count; i++)
            if (pool[i].Spell.Level != pool[i - 1].Spell.Level) ranks++;
        double total = 0, weight = 1;
        for (int rank = 0; rank < ranks; rank++) { total += weight; weight *= 0.22; }

        double roll = Random.Shared.NextDouble() * total;
        int chosen = 0;
        weight = 1;
        for (int rank = 0; rank < ranks; rank++)
        {
            roll -= weight;
            if (roll <= 0) { chosen = rank; break; }
            weight *= 0.22;
        }
        int start = 0, current = 0;
        for (int i = 1; i < pool.Count && current < chosen; i++)
            if (pool[i].Spell.Level != pool[i - 1].Spell.Level) { current++; start = i; }
        int end = start + 1;
        while (end < pool.Count && pool[end].Spell.Level == pool[start].Spell.Level) end++;
        return pool[start + Random.Shared.Next(end - start)];
    }

    public static (Spell Spell, SpellLine Line) ChooseBonedancerMinion(
        GameLiving owner,
        IEnumerable<(Spell Spell, SpellLine Line)> available)
    {
        (Spell Spell, SpellLine Line)[] all = available.Where(entry => entry.Spell != null).ToArray();
        if (all.Length == 0)
            return default;

        eSpecType spec = owner is GameBot bot ? bot.BotSpec?.SpecType ?? eSpecType.None : eSpecType.None;
        (Spell Spell, SpellLine Line)[] preferred = all.Where(entry => IsPreferredBonedancerSubPet(spec, entry.Spell)).ToArray();

        if (owner is GameBot { IsEndgameCompanion: true })
        {
            int highestLevel = all.Max(entry => entry.Spell.Level);
            var highest = all.Where(entry => entry.Spell.Level == highestLevel).ToArray();
            var bestForBuild = highest.Where(entry => IsPreferredBonedancerSubPet(spec, entry.Spell)).ToArray();
            return ChooseWeightedByRank(bestForBuild.Length > 0 ? bestForBuild : highest, true);
        }

        // Seventy-eight percent favors the build's natural subpet roles. The
        // remaining rolls use the complete legal list, preserving variety.
        IEnumerable<(Spell Spell, SpellLine Line)> pool = preferred.Length > 0 && Random.Shared.NextDouble() < 0.78
            ? preferred
            : all;
        return ChooseWeightedByRank(pool);
    }

    private static (Spell Spell, SpellLine Line) ChooseBonedancerMinion(
        GameLiving owner,
        CommanderPet commander,
        int desiredCount,
        IEnumerable<(Spell Spell, SpellLine Line)> available)
    {
        if (owner == null || commander == null || desiredCount <= 0)
            return default;

        (Spell Spell, SpellLine Line, int PetLevel, bool Preferred)[] candidates = available
            .Where(entry => entry.Spell != null)
            .DistinctBy(entry => entry.Spell.ID)
            .Select(entry => (
                entry.Spell,
                entry.Line,
                PetLevel: BonedancerSubPetLevel(owner.Level, entry.Spell),
                Preferred: IsPreferredBonedancerSubPet(owner is GameBot bot ? bot.BotSpec?.SpecType ?? eSpecType.None : eSpecType.None, entry.Spell)))
            .Where(entry => entry.PetLevel > 0 && entry.PetLevel <= 75)
            .ToArray();
        if (candidates.Length == 0)
            return default;

        int activeCount;
        int activeLevels;
        lock (commander.ControlledNpcListLock)
        {
            var active = commander.ControlledNpcList?.Where(brain => brain?.Body is
            {
                IsAlive: true,
                ObjectState: GameObject.eObjectState.Active
            }).ToArray() ?? [];
            activeCount = active.Length;
            activeLevels = active.Sum(brain => (int)brain.Body.Level);
        }

        int slotsToFill = Math.Min(desiredCount, commander.SubPetCapacity) - activeCount;
        int remainingBudget = 75 - activeLevels;
        if (slotsToFill <= 0 || remainingBudget <= 0)
            return default;

        (Spell Spell, SpellLine Line) best = default;
        int bestTotal = -1;
        int bestPreferred = -1;
        int bestRank = -1;

        // A rank can be summoned more than once. Enumerating at most three
        // slots lets us choose the complete legal composition before casting
        // its first member: two strong pets or three weaker pets, with the
        // resulting level sum as close to 75 as the learned ranks permit.
        void Search(int depth, int total, int preferred, int rankScore,
            (Spell Spell, SpellLine Line) first)
        {
            if (depth == slotsToFill)
            {
                if (total > remainingBudget)
                    return;
                if (total > bestTotal || total == bestTotal && preferred > bestPreferred ||
                    total == bestTotal && preferred == bestPreferred && rankScore > bestRank)
                {
                    best = first;
                    bestTotal = total;
                    bestPreferred = preferred;
                    bestRank = rankScore;
                }
                return;
            }

            foreach (var candidate in candidates)
            {
                int nextTotal = total + candidate.PetLevel;
                if (nextTotal > remainingBudget)
                    continue;
                Search(depth + 1, nextTotal, preferred + (candidate.Preferred ? 1 : 0),
                    rankScore + candidate.Spell.Level,
                    depth == 0 ? (candidate.Spell, candidate.Line) : first);
            }
        }

        Search(0, 0, 0, 0, default);
        return best;
    }

    public static int BonedancerSubPetLevel(int ownerLevel, Spell spell)
    {
        if (spell == null || ownerLevel <= 0)
            return 0;
        double raw = spell.Damage < 0
            ? ownerLevel * spell.Damage * -0.01
            : spell.Damage;
        int level = Math.Max(1, (int)raw);
        if (spell.Value > 0)
            level = Math.Min(level, (int)spell.Value);
        return Math.Min(byte.MaxValue, level);
    }

    public static int BonedancerDesiredMinionCount(int capacity, double roll) => Math.Clamp(capacity, 0, 3) switch
    {
        3 => roll < 0.5 ? 2 : 3,
        int count => count,
    };

    private static int DesiredBonedancerMinionCount(CommanderPet commander)
    {
        int capacity = Math.Min(3, commander?.SubPetCapacity ?? 0);
        if (capacity < 3)
            return capacity;

        int desired = commander.TempProperties.GetProperty<int>(BonedancerDesiredMinionCountProperty);
        if (desired is 2 or 3)
            return desired;
        desired = BonedancerDesiredMinionCount(capacity, Random.Shared.NextDouble());
        commander.TempProperties.SetProperty(BonedancerDesiredMinionCountProperty, desired);
        return desired;
    }

    public static bool IsPreferredBonedancerSubPet(eSpecType spec, Spell spell)
    {
        if (spell == null || spell.SpellType != eSpellType.SummonMinion)
            return false;

        BdSubPet.SubPetType type = (BdSubPet.SubPetType)spell.DamageType;
        return spec switch
        {
            eSpecType.DarkBone => type is BdSubPet.SubPetType.Caster or BdSubPet.SubPetType.Debuffer,
            eSpecType.SuppBone => type is BdSubPet.SubPetType.Healer or BdSubPet.SubPetType.Buffer,
            eSpecType.ArmyBone => type is BdSubPet.SubPetType.Melee or BdSubPet.SubPetType.Archer,
            _ => false,
        };
    }

    private static bool CanAddBonedancerMinion(CommanderPet commander, int desiredCount)
    {
        IControlledBrain[] minions = commander?.ControlledNpcList;
        if (minions == null)
            return false;

        lock (commander.ControlledNpcListLock)
        {
            int capacity = Math.Min(Math.Min(3, minions.Length), Math.Max(0, desiredCount));
            int active = minions.Count(brain => brain?.Body is
            {
                IsAlive: true,
                ObjectState: GameObject.eObjectState.Active
            });
            return active < capacity;
        }
    }

    private static void PruneBonedancerMinionSlots(CommanderPet commander)
    {
        IControlledBrain[] minions = commander?.ControlledNpcList;
        if (minions == null)
            return;

        IControlledBrain[] stale;
        lock (commander.ControlledNpcListLock)
        {
            stale = minions
                .Where(brain => brain?.Body is not
                {
                    IsAlive: true,
                    ObjectState: GameObject.eObjectState.Active
                })
                .ToArray();
        }

        // Remove outside the enumeration lock; RemoveControlledBrain takes
        // the same lock and also strips any effects owned by the stale minion.
        foreach (IControlledBrain brain in stale)
            commander.RemoveControlledBrain(brain);
    }

    private static bool ShouldUpgradeBonedancerCommander(
        GameBot owner,
        CommanderPet commander,
        IEnumerable<(Spell Spell, SpellLine Line)> knownSpells,
        GameLiving combatTarget)
    {
        if (owner == null || commander == null || knownSpells == null ||
            owner.InCombat || owner.IsAttacking || owner.IsCasting ||
            commander.InCombat || commander.IsAttacking ||
            combatTarget?.IsAlive == true)
            return false;

        (Spell Spell, SpellLine Line) strongest = knownSpells
            .Where(entry => entry.Spell?.SpellType == eSpellType.SummonCommander &&
                            CanCast(owner, entry.Spell))
            .OrderByDescending(entry => entry.Spell.Level)
            .FirstOrDefault();

        if (strongest.Spell == null)
            return false;

        return CommanderRank(commander.CommanderType) < strongest.Spell.Level;
    }

    private static int CommanderRank(CommanderPet.eCommanderType commanderType) => commanderType switch
    {
        CommanderPet.eCommanderType.ReturnedCommander => 1,
        CommanderPet.eCommanderType.DecayedCommander => 1,
        CommanderPet.eCommanderType.SkeletalCommander => 15,
        CommanderPet.eCommanderType.BoneCommander => 30,
        CommanderPet.eCommanderType.DreadCommander => 45,
        CommanderPet.eCommanderType.DreadArcher => 45,
        CommanderPet.eCommanderType.DreadGuardian => 45,
        CommanderPet.eCommanderType.DreadLich => 45,
        CommanderPet.eCommanderType.DreadLord => 45,
        _ => 0
    };

    internal static void PrepareAnimistGroundTarget(GameLiving owner, GameLiving combatTarget, Spell spell)
    {
        if (spell == null || spell.SpellType is not (eSpellType.SummonAnimistPet or eSpellType.SummonAnimistFnF or eSpellType.SummonAnimistFnFCustom or eSpellType.SummonAnimistAmbusher))
            return;

        int range = Math.Max(100, spell.CalculateEffectiveRange(owner));
        Vector3 ownerPosition = new(owner.X, owner.Y, owner.Z);
        Vector3 desired;
        if (combatTarget != null)
        {
            Vector3 target = new(combatTarget.X, combatTarget.Y, combatTarget.Z);
            Vector3 backTowardOwner = ownerPosition - target;
            if (backTowardOwner.LengthSquared() < 1)
                backTowardOwner = Vector3.UnitX;
            // Keep the grove tight enough to share a pull, but never stack every
            // turret on the same coordinate. This also makes collision and LoS
            // failures independent instead of duplicating one bad placement.
            double angle = Random.Shared.NextDouble() * Math.PI * 2;
            float radius = Random.Shared.Next(55, 131);
            Vector3 spread = new((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0);
            desired = target + Vector3.Normalize(backTowardOwner) * 110 + spread;
            if (Vector3.Distance(ownerPosition, desired) > range - 40)
                desired = ownerPosition + Vector3.Normalize(desired - ownerPosition) * (range - 40);
        }
        else
        {
            double heading = owner.Heading * Point2D.HEADING_TO_RADIAN;
            desired = new(
                owner.X - (float)Math.Sin(heading) * 120,
                owner.Y + (float)Math.Cos(heading) * 120,
                owner.Z);
        }

        SetAnimistGroundTarget(owner, desired);
    }

    /// <summary>Ground-targets a shroom spot, slid along the navmesh from the caster so it lands on walkable ground.</summary>
    internal static void SetAnimistGroundTarget(GameLiving owner, Vector3 desired)
    {
        Vector3 ownerPosition = new(owner.X, owner.Y, owner.Z);
        Vector3 snapped = PathfindingProvider.Instance.GetMoveAlongSurface(
            owner.CurrentZone,
            ownerPosition,
            desired,
            PathfindingProvider.Instance.DefaultFilters) ?? desired;
        owner.SetGroundTarget((int)snapped.X, (int)snapped.Y, (int)snapped.Z);
        owner.GroundTargetInView = true;
    }

    public static bool NeedsNecromancerPetCommand(GameLiving target, Spell command)
    {
        // The shade's wrapper never becomes an effect. The servant applies
        // SubSpellID instead; testing the wrapper caused a 15-second retry
        // forever, even after a successful twenty-minute buff.
        if (target == null || !TryGetNecromancerPetPayload(command, out Spell payload))
            return false;
        if (payload.IsHealing && target.HealthPercent >= 88)
            return false;
        return payload.Duration <= 0 || !HasEffect(target, payload);
    }

    /// <summary>
    /// Only the actual long-lived Sluaghbinder pet-buff ranks use the faster
    /// between-buff cadence. The instant Cairnheart pet heals and every other
    /// class/spec retain their existing cast and retry rules.
    /// </summary>
    public static bool IsCovenantRoutinePetBuff(eCharacterClass characterClass, eSpecType specType, Spell spell) =>
        characterClass == eCharacterClass.Sluaghbinder &&
        specType == eSpecType.SluaghbinderCovenant &&
        spell != null &&
        (spell.ID is >= 59006 and <= 59010 or >= 59045 and <= 59064) &&
        spell.Target == eSpellTarget.PET && spell.IsBuff &&
        spell.CastTime > 0 && spell.Duration > 30_000 &&
        EffectHelper.GetEffectFromSpell(spell) is not (eEffect.Unknown or eEffect.Pet);

    /// <summary>
    /// Long pet buffs of every pet class use the quick between-buff cadence the
    /// Sluaghbinder Covenant had first: the next pet action follows the cast by
    /// half a second, a cast whose effect has not shown up stays deferred for the
    /// old bounded retry window, and combat keeps the old 15-second spacing.
    /// Necromancer servant commands, pet heals and short tactical effects keep
    /// their own rules.
    /// </summary>
    public static bool IsFastRoutinePetBuff(eCharacterClass characterClass, eSpecType specType, Spell spell) =>
        IsCovenantRoutinePetBuff(characterClass, specType, spell) ||
        spell != null && spell.Target == eSpellTarget.PET && spell.IsBuff && !spell.IsHealing &&
        spell.SpellType != eSpellType.PetSpell && spell.Duration > 30_000 &&
        !IsCovenantPetHot(characterClass, specType, spell) &&
        EffectHelper.GetEffectFromSpell(spell) is not (eEffect.Unknown or eEffect.Pet);

    public static int PetBuffActionCooldown(
        eCharacterClass characterClass, eSpecType specType, Spell spell, bool combatOwnsTurn) =>
        IsFastRoutinePetBuff(characterClass, specType, spell) && !combatOwnsTurn
            ? PetActionCooldown(spell)
            : PetBuffRetryCooldown(spell);

    private static bool IsCovenantPetBuffDeferred(GameNPC pet, long now, bool combatOwnsTurn)
    {
        if (!CovenantPetBuffCadences.TryGetValue(pet, out CovenantPetBuffCadenceState cadence))
            return false;
        (eEffect pendingEffect, int pendingSpellId) = cadence.PendingSpell;
        bool effectPresent = pendingEffect != eEffect.Unknown &&
            pet.effectListComponent.GetSpellEffects(pendingEffect)
                .Any(effect => (effect.IsActive || effect.IsDisabled) &&
                    effect.ExpireTick > now &&
                    effect.SpellHandler?.Spell?.ID == pendingSpellId);
        return cadence.IsDeferred(now, combatOwnsTurn, effectPresent);
    }

    /// <summary>
    /// The five Cairnheart Covenant ranks are the spec's pet heal-over-time.
    /// They are true HealOverTime spells (15 s, pulsing every 3 s); older
    /// databases stored them as one-minute HealthRegenBuff. Either form stays
    /// in the low-health heal path only; the generic pet-buff path would
    /// refresh them on a healthy pet and prevent rest. Other classes' buffs
    /// and heals are untouched.
    /// </summary>
    public static bool IsCovenantPetHot(eCharacterClass characterClass, eSpecType specType, Spell spell) =>
        characterClass == eCharacterClass.Sluaghbinder &&
        specType == eSpecType.SluaghbinderCovenant &&
        IsCairnheartPetHotRank(spell);

    private static bool IsCairnheartPetHotRank(Spell spell) =>
        spell is
        {
            ID: >= 59065 and <= 59069,
            Target: eSpellTarget.PET,
        } &&
        (spell.SpellType == eSpellType.HealOverTime ||
         spell.SpellType == eSpellType.HealthRegenBuff && spell.Duration >= 60_000);

    public static bool ShouldSkipActiveCovenantPetHot(
        eCharacterClass characterClass,
        eSpecType specType,
        Spell spell,
        bool petHasActiveEffect) =>
        petHasActiveEffect && IsCovenantPetHot(characterClass, specType, spell);

    private static bool HasActiveCovenantPetHot(GameLiving pet) =>
        pet.effectListComponent.GetSpellEffects(eEffect.HealOverTime)
            .Concat(pet.effectListComponent.GetSpellEffects(eEffect.HealthRegenBuff))
            .Any(effect => (effect.IsActive || effect.IsDisabled) &&
                effect.ExpireTick > GameLoop.GameLoopTime &&
                IsCairnheartPetHotRank(effect.SpellHandler?.Spell));

    public static bool TryGetNecromancerPetPayload(Spell command, out Spell payload)
    {
        payload = command?.SpellType == eSpellType.PetSpell && command.SubSpellID > 0
            ? SkillBase.GetSpellByID(command.SubSpellID)
            : null;
        return payload != null && payload.SubSpellID == 0;
    }

    public static bool IsInterruptibleNecromancerServantBuff(Spell command) =>
        TryGetNecromancerPetPayload(command, out Spell payload) &&
        payload.IsBuff && payload.CastTime > 0;

    private static bool HasEffect(GameLiving target, Spell spell)
    {
        eEffect effect = EffectHelper.GetEffectFromSpell(spell);
        return effect is not (eEffect.Unknown or eEffect.DirectDamage or eEffect.Pet) &&
               EffectListService.GetEffectOnTarget(target, effect) != null;
    }
}
