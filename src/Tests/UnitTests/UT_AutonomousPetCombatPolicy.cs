using System.Linq;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Spells;
using DOL.GS.PlayerClass;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousPetCombatPolicy
{
    [Test]
    public void GeneratedCharmTemplatesStayInTheirOwnersRealm()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(eCharacterClass.Sorcerer, 1), Is.True);
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(eCharacterClass.Sorcerer, 200), Is.False);
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(eCharacterClass.Minstrel, 1), Is.True);
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(eCharacterClass.Minstrel, 200), Is.False);
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(eCharacterClass.Mentalist, 200), Is.True);
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(eCharacterClass.Mentalist, 100), Is.False);
        });
    }

    [Test]
    public void DruidPetIsDefensiveOnly()
    {
        Assert.That(AutonomousPetSupport.IsDefensiveOnlyPetClass(eCharacterClass.Druid), Is.True);
        Assert.That(AutonomousPetSupport.IsDefensiveOnlyPetClass(eCharacterClass.Hunter), Is.False);
        Assert.That(AutonomousPetSupport.IsDefensiveOnlyPetClass(eCharacterClass.Minstrel), Is.True);
        Assert.That(AutonomousPetSupport.IsDefensiveOnlyPetClass(eCharacterClass.Mentalist), Is.False);
        Assert.That(AutonomousPetSupport.IsDefensiveOnlyPetClass(eCharacterClass.Enchanter), Is.False);
        Assert.That(AutonomousPetSupport.IsDefensiveOnlyPetClass(eCharacterClass.Bonedancer), Is.False);
    }

    [TestCase(eCharacterClass.Enchanter)]
    [TestCase(eCharacterClass.Cabalist)]
    [TestCase(eCharacterClass.Spiritmaster)]
    [TestCase(eCharacterClass.Sorcerer)]
    [TestCase(eCharacterClass.Mentalist)]
    [TestCase(eCharacterClass.Hunter)]
    public void PrimaryCasterPetOwnerNeverSerializesBehindPet(eCharacterClass characterClass)
    {
        Assert.That(AutonomousPetSupport.UsesIndependentOwnerPetCombat(characterClass), Is.True);
    }

    [Test]
    public void PlayerBotCasterNeverUsesItsHumanOwnerAsClientLosProxy()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NpcCastingComponent.ShouldUseControlledOwnerLosProxy(true, true), Is.False,
                "A GameBot is an independent actor even though BotBrain implements IControlledBrain.");
            Assert.That(NpcCastingComponent.ShouldUseControlledOwnerLosProxy(false, true), Is.True,
                "A real controlled summon retains the normal player-client LoS proxy.");
            Assert.That(NpcCastingComponent.ShouldUseControlledOwnerLosProxy(false, false), Is.False);
        });
    }

    [TestCase(eCharacterClass.Bonedancer)]
    [TestCase(eCharacterClass.Spiritmaster)]
    [TestCase(eCharacterClass.Cabalist)]
    [TestCase(eCharacterClass.Enchanter)]
    [TestCase(eCharacterClass.Hunter)]
    [TestCase(eCharacterClass.Druid)]
    public void ControlledPetDefendsAutonomousOwner(eCharacterClass ownerClass)
    {
        Assert.That(ControlledMobBrain.PetMayDefendOwner(ownerClass), Is.True);
    }

    [Test]
    public void TheurgistElementalKeepsItsOrderedTargetInsteadOfOwnerDefense()
    {
        Assert.That(ControlledMobBrain.PetMayDefendOwner(eCharacterClass.Theurgist), Is.False);
    }

    [Test]
    public void BonedancerSupportMinionsDoNotWaitForAPlayerClientLosReply()
    {
        Assert.That(BdPetBrain.UsesPlayerClientLosForSupportCasts, Is.False,
            "Patroller/Mender support must complete without a nearby player's LoS packet.");
    }

    [TestCase(eCharacterClass.Druid)]
    [TestCase(eCharacterClass.Minstrel)]
    [TestCase(eCharacterClass.Bonedancer)]
    public void DedicatedAndDefensivePetClassesKeepSeparatePolicy(eCharacterClass characterClass)
    {
        Assert.That(AutonomousPetSupport.UsesIndependentOwnerPetCombat(characterClass), Is.False);
    }

    [TestCase(eCharacterClass.Bonedancer)]
    [TestCase(eCharacterClass.Enchanter)]
    [TestCase(eCharacterClass.Cabalist)]
    [TestCase(eCharacterClass.Spiritmaster)]
    [TestCase(eCharacterClass.Minstrel)]
    public void OwnerPetSpellsHaveOneAutonomousUpkeepOwner(eCharacterClass characterClass)
    {
        Assert.That(AutonomousPetSupport.OwnsPetUpkeep(characterClass), Is.True);
    }

    [Test]
    public void BonedancerKeepsCommanderTreePolicyWhileGenericPetBuffsAreExcluded()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.OwnsPetUpkeep(eCharacterClass.Bonedancer), Is.True);
            Assert.That(AutonomousPetSupport.UsesIndependentOwnerPetCombat(eCharacterClass.Bonedancer), Is.False);
        });
    }

    [Test]
    public void GeneratedCharmProfilesUseRequestedUnlocksAndExactOffsets()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.TryGetGeneratedCharmProfile(eCharacterClass.Hunter, out _), Is.False,
                "Hunter bots and companions must retain their existing wolf summon behavior");
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(eCharacterClass.Hunter, 100), Is.True);
            Assert.That(AutonomousPetSupport.IsGeneratedCharmTemplateRegion(eCharacterClass.Hunter, 200), Is.False);
            Assert.That(AutonomousPetSupport.GeneratedCharmTargetLevel(eCharacterClass.Mentalist, 3), Is.Zero);
            Assert.That(AutonomousPetSupport.GeneratedCharmTargetLevel(eCharacterClass.Mentalist, 4), Is.EqualTo(1));
            Assert.That(AutonomousPetSupport.GeneratedCharmTargetLevel(eCharacterClass.Mentalist, 20), Is.EqualTo(17));
            Assert.That(AutonomousPetSupport.GeneratedCharmTargetLevel(eCharacterClass.Sorcerer, 6), Is.Zero);
            Assert.That(AutonomousPetSupport.GeneratedCharmTargetLevel(eCharacterClass.Sorcerer, 7), Is.EqualTo(5));
            Assert.That(AutonomousPetSupport.GeneratedCharmTargetLevel(eCharacterClass.Sorcerer, 20), Is.EqualTo(18));
            Assert.That(AutonomousPetSupport.GeneratedCharmTargetLevel(eCharacterClass.Minstrel, 5), Is.Zero);
            Assert.That(AutonomousPetSupport.GeneratedCharmTargetLevel(eCharacterClass.Minstrel, 6), Is.EqualTo(3));
            Assert.That(AutonomousPetSupport.GeneratedCharmTargetLevel(eCharacterClass.Minstrel, 20), Is.EqualTo(17));
        });
    }

    [Test]
    public void SyntheticCharmCandidatesNeverSpawnInsideCapitals()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.CanCreateSyntheticCharmInRegion(true), Is.False);
            Assert.That(AutonomousPetSupport.CanCreateSyntheticCharmInRegion(false), Is.True);
        });
    }

    [Test]
    public void MinstrelUsesRangedMagicUntilPersonallyPressedIntoMelee()
    {
        Assert.That(BotBrain.PrefersSpellRange(new ClassMinstrel()), Is.True);
    }

    [Test]
    public void GroupedMinstrelUsesMeleeAndCommandsPetWithoutChangingSoloOrOtherClasses()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotBrain.PrefersSpellRange(new ClassMinstrel(), true), Is.False);
            Assert.That(BotBrain.PrefersSpellRange(new ClassMinstrel(), false), Is.True);
            Assert.That(BotBrain.PrefersSpellRange(new ClassSorcerer(), true), Is.True);
            Assert.That(AutonomousPetSupport.IsDefensiveOnlyPetClass(eCharacterClass.Minstrel, true), Is.False);
            Assert.That(AutonomousPetSupport.IsDefensiveOnlyPetClass(eCharacterClass.Minstrel, false), Is.True);
            Assert.That(AutonomousPetSupport.IsDefensiveOnlyPetClass(eCharacterClass.Druid, true), Is.True);
            Assert.That(AutonomousPetSupport.IsDefensiveOnlyPetClass(eCharacterClass.Hunter, true), Is.False);
        });
    }

    [Test]
    public void MentalistAndSorcererAreAttackerBuffers()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotPartyRoles.For(eCharacterClass.Mentalist), Is.EqualTo(BotPartyRole.Damage));
            Assert.That(BotPartyRoles.For(eCharacterClass.Sorcerer), Is.EqualTo(BotPartyRole.Damage));
            Assert.That(BotPartyRoles.Label(eCharacterClass.Mentalist), Is.EqualTo("Attacker/Buffer"));
            Assert.That(BotPartyRoles.Label(eCharacterClass.Sorcerer), Is.EqualTo("Attacker/Buffer"));
        });
    }

    [TestCase(eCharacterClass.Bard)]
    [TestCase(eCharacterClass.Minstrel)]
    [TestCase(eCharacterClass.Skald)]
    public void ClassicSongClassesAreAttackersAndBuffers(eCharacterClass characterClass)
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotPartyRoles.For(characterClass), Is.EqualTo(BotPartyRole.Damage));
            Assert.That(BotPartyRoles.CanFill(characterClass, BotPveGroupRole.Attacker), Is.True);
            Assert.That(BotPartyRoles.CanFill(characterClass, BotPveGroupRole.Buffer), Is.True);
            Assert.That(BotPartyRoles.Label(characterClass), Is.EqualTo("Attacker/Buffer"));
        });
    }

    [Test]
    public void ShortPetDamageShieldCannotStarveTravelButRemainsAvailableInCombat()
    {
        Spell auraOfEchoing = new(new DbSpell
        {
            SpellID = 4451,
            Name = "Aura of Echoing",
            Target = eSpellTarget.PET.ToString(),
            Type = eSpellType.DamageShield.ToString(),
            Duration = 5,
            CastTime = 2.5,
        }, 1);

        Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(auraOfEchoing, false), Is.False);
        Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(auraOfEchoing, true), Is.True);
    }

    [Test]
    public void LongPetStatBuffRemainsNormalOutOfCombatUpkeep()
    {
        Spell petStats = new(new DbSpell
        {
            SpellID = 4771,
            Name = "Craftiness",
            Target = eSpellTarget.PET.ToString(),
            Type = eSpellType.DexterityQuicknessBuff.ToString(),
            Duration = 1200,
            CastTime = 3,
        }, 1);

        Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(petStats, false), Is.True);
    }

    [TestCase(59006, eSpellType.StrengthConstitutionBuff, 3.0, 3500)]
    [TestCase(59049, eSpellType.DexterityQuicknessBuff, 3.0, 3500)]
    [TestCase(59054, eSpellType.SpecArmorFactorBuff, 3.0, 3500)]
    [TestCase(59059, eSpellType.DamageAdd, 3.0, 3500)]
    [TestCase(59064, eSpellType.CombatSpeedBuff, 3.5, 4000)]
    public void CovenantPetBuffsCanAdvanceAfterTheirRealCastTime(
        int spellId, eSpellType type, double castSeconds, int nextActionMilliseconds)
    {
        Spell petBuff = new(new DbSpell
        {
            SpellID = spellId,
            Name = "Covenant pet buff",
            Target = eSpellTarget.PET.ToString(),
            Type = type.ToString(),
            Duration = 1200,
            CastTime = castSeconds,
        }, 1);

        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.IsCovenantRoutinePetBuff(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, petBuff), Is.True);
            Assert.That(AutonomousPetSupport.PetBuffActionCooldown(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, petBuff, false),
                Is.EqualTo(nextActionMilliseconds), "The next distinct buff need not wait fifteen seconds");
            Assert.That(AutonomousPetSupport.PetBuffActionCooldown(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, petBuff, true),
                Is.EqualTo(15_000), "Keep the old buff pacing during combat");
            Assert.That(AutonomousPetSupport.IsCovenantRoutinePetBuff(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderBane, petBuff), Is.False);
            Assert.That(AutonomousPetSupport.PetBuffActionCooldown(
                eCharacterClass.Enchanter, eSpecType.SluaghbinderCovenant, petBuff, false),
                Is.EqualTo(nextActionMilliseconds), "Every pet class now buffs at the cast cadence out of combat");
            Assert.That(AutonomousPetSupport.PetBuffActionCooldown(
                eCharacterClass.Enchanter, eSpecType.SluaghbinderCovenant, petBuff, true),
                Is.EqualTo(15_000), "Combat keeps the old spacing for every class");
        });
    }

    [Test]
    public void CovenantPetBuffWaitsForEffectConfirmationOrRetryWindow()
    {
        AutonomousPetSupport.CovenantPetBuffCadenceState cadence = new();
        cadence.Record(eEffect.DexQuickBuff, 59049, 16_000);

        Assert.Multiple(() =>
        {
            Assert.That(cadence.PendingSpell, Is.EqualTo((eEffect.DexQuickBuff, 59049)));
            Assert.That(cadence.IsDeferred(4_500, false, false), Is.True,
                "A missing effect must retain the old bounded retry delay");
            Assert.That(cadence.IsDeferred(4_500, false, true), Is.False,
                "The next distinct buff can follow once the previous effect is confirmed");
            Assert.That(cadence.IsDeferred(4_500, true, true), Is.True,
                "Combat must not be monopolized by a burst of pet buffs");
            Assert.That(cadence.IsDeferred(16_000, false, false), Is.False,
                "An unconfirmed buff can retry when its bounded wait expires");
            Assert.That(cadence.IsDeferred(16_000, true, false), Is.False);
        });
    }

    [Test]
    public void InstantCairnheartHealAndOtherSpecsDoNotEnterPetBuffCadence()
    {
        Spell petHeal = new(new DbSpell
        {
            SpellID = 59069,
            Name = "Cairnheart Rebirth",
            Target = eSpellTarget.PET.ToString(),
            Type = eSpellType.HealthRegenBuff.ToString(),
            Duration = 60,
            CastTime = 0,
        }, 1);
        Spell otherPetBuff = new(new DbSpell
        {
            SpellID = 4771,
            Name = "Craftiness",
            Target = eSpellTarget.PET.ToString(),
            Type = eSpellType.DexterityQuicknessBuff.ToString(),
            Duration = 1200,
            CastTime = 3,
        }, 1);

        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.IsCovenantRoutinePetBuff(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, petHeal), Is.False);
            Assert.That(AutonomousPetSupport.IsCovenantRoutinePetBuff(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, otherPetBuff), Is.False);
            Assert.That(AutonomousPetSupport.IsCovenantRoutinePetBuff(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderBulwark, otherPetBuff), Is.False);
        });
    }

    [TestCase(59065, "Cairnheart Pulse")]
    [TestCase(59066, "Cairnheart Mending")]
    [TestCase(59067, "Cairnheart Rekindling")]
    [TestCase(59068, "Cairnheart Resurgence")]
    [TestCase(59069, "Cairnheart Rebirth")]
    public void EveryLiveCovenantPetHotRankWaitsForItsEffectToExpire(int spellId, string name)
    {
        Spell petHot = new(new DbSpell
        {
            SpellID = spellId,
            Name = name,
            Target = eSpellTarget.PET.ToString(),
            Type = eSpellType.HealthRegenBuff.ToString(),
            Duration = 60,
            CastTime = 0,
        }, 1);

        Assert.Multiple(() =>
        {
            Assert.That(petHot.IsHealing, Is.True);
            Assert.That(petHot.IsBuff, Is.True);
            Assert.That(petHot.CastTime, Is.Zero);
            Assert.That(petHot.Duration, Is.EqualTo(60_000));
            Assert.That(EffectHelper.GetEffectFromSpell(petHot), Is.EqualTo(eEffect.HealthRegenBuff));
            Assert.That(AutonomousPetSupport.IsCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, petHot), Is.True,
                "Keep this healing rank out of routine pet-buff upkeep");
            Assert.That(AutonomousPetSupport.ShouldSkipActiveCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, petHot, true), Is.True,
                "Do not restart the one-minute pet heal while any Cairnheart rank is still running");
            Assert.That(AutonomousPetSupport.ShouldSkipActiveCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, petHot, false), Is.False,
                "Expiration or dispel must allow healing an injured pet again");
            Assert.That(AutonomousPetSupport.ShouldSkipActiveCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderBane, petHot, true), Is.False);
            Assert.That(AutonomousPetSupport.ShouldSkipActiveCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderBulwark, petHot, true), Is.False);
            Assert.That(AutonomousPetSupport.ShouldSkipActiveCovenantPetHot(
                eCharacterClass.Enchanter, eSpecType.SluaghbinderCovenant, petHot, true), Is.False);
        });
    }

    [TestCase(59065, "Cairnheart Pulse")]
    [TestCase(59066, "Cairnheart Mending")]
    [TestCase(59067, "Cairnheart Rekindling")]
    [TestCase(59068, "Cairnheart Resurgence")]
    [TestCase(59069, "Cairnheart Rebirth")]
    public void RebalancedCovenantPetHotRanksUseTheSameHealGuard(int spellId, string name)
    {
        // The balance pass turned the Cairnheart ranks into real 15-second
        // heals-over-time (pulse every 3 s, 20 s recast).
        Spell petHot = new(new DbSpell
        {
            SpellID = spellId,
            Name = name,
            Target = eSpellTarget.PET.ToString(),
            Type = eSpellType.HealOverTime.ToString(),
            Duration = 15,
            Frequency = 30,
            RecastDelay = 20,
            CastTime = 0,
        }, 1);

        Assert.Multiple(() =>
        {
            Assert.That(petHot.IsHealing, Is.True);
            Assert.That(EffectHelper.GetEffectFromSpell(petHot), Is.EqualTo(eEffect.HealOverTime));
            Assert.That(AutonomousPetSupport.IsCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, petHot), Is.True);
            Assert.That(AutonomousPetSupport.ShouldSkipActiveCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, petHot, true), Is.True,
                "Do not restart the pet heal-over-time while any Cairnheart rank is still running");
            Assert.That(AutonomousPetSupport.ShouldSkipActiveCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, petHot, false), Is.False);
            Assert.That(AutonomousPetSupport.ShouldSkipActiveCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderBane, petHot, true), Is.False);
        });
    }

    [Test]
    public void CovenantPetHealGuardDoesNotSuppressDirectOrShortHealingSpells()
    {
        Spell directHeal = new(new DbSpell
        {
            SpellID = 59091,
            Name = "Pet Direct Heal",
            Target = eSpellTarget.PET.ToString(),
            Type = eSpellType.Heal.ToString(),
            Duration = 60,
        }, 1);
        Spell shortHot = new(new DbSpell
        {
            SpellID = 59092,
            Name = "Short Pet HoT",
            Target = eSpellTarget.PET.ToString(),
            Type = eSpellType.HealOverTime.ToString(),
            Duration = 30,
        }, 1);
        Spell ordinaryPetRegen = new(new DbSpell
        {
            SpellID = 59090,
            Name = "Other pet regeneration",
            Target = eSpellTarget.PET.ToString(),
            Type = eSpellType.HealthRegenBuff.ToString(),
            Duration = 60,
        }, 1);
        Spell cairnheartStats = new(new DbSpell
        {
            SpellID = 59009,
            Name = "Cairnheart Renewal",
            Target = eSpellTarget.PET.ToString(),
            Type = eSpellType.StrengthConstitutionBuff.ToString(),
            Duration = 1200,
        }, 1);

        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.ShouldSkipActiveCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, directHeal, true), Is.False);
            Assert.That(AutonomousPetSupport.ShouldSkipActiveCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, shortHot, true), Is.False);
            Assert.That(AutonomousPetSupport.IsCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, ordinaryPetRegen), Is.False);
            Assert.That(AutonomousPetSupport.IsCovenantPetHot(
                eCharacterClass.Sluaghbinder, eSpecType.SluaghbinderCovenant, cairnheartStats), Is.False);
        });
    }

    [TestCase(eCharacterClass.Enchanter)]
    [TestCase(eCharacterClass.Cabalist)]
    [TestCase(eCharacterClass.Spiritmaster)]
    [TestCase(eCharacterClass.Sorcerer)]
    [TestCase(eCharacterClass.Bonedancer)]
    public void PrimaryCasterPetsCannotStarveInitialGoalOrTravel(eCharacterClass characterClass)
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.ShouldPrioritizeWorldMovement(
                characterClass, true, false, false, false, false, false, "Choosing first live goal"), Is.True);
            Assert.That(AutonomousPetSupport.ShouldPrioritizeWorldMovement(
                characterClass, true, false, false, false, true, true, "Traveling to camp"), Is.True);
            Assert.That(AutonomousPetSupport.ShouldPrioritizeWorldMovement(
                characterClass, true, false, false, true, false, true, "Fighting"), Is.False);
            Assert.That(AutonomousPetSupport.ShouldPrioritizeWorldMovement(
                characterClass, true, false, false, false, false, true, "At live camp"), Is.False);
        });
    }

    [Test]
    public void MovementFirstPetGateNeverChangesCompanionOrOtherPetClasses()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.ShouldPrioritizeWorldMovement(
                eCharacterClass.Enchanter, true, true, false, false, false, false, "Choosing"), Is.False);
            Assert.That(AutonomousPetSupport.ShouldPrioritizeWorldMovement(
                eCharacterClass.Enchanter, true, false, true, false, false, false, "Choosing"), Is.False);
            Assert.That(AutonomousPetSupport.ShouldPrioritizeWorldMovement(
                eCharacterClass.Druid, true, false, false, false, false, false, "Choosing"), Is.False);
        });
    }

    [Test]
    public void LearnedBonedancerSubpetBypassesTravelFirstOnlyWhileCommanderSlotIsEmpty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.IsRequiredBonedancerArmyGap(1, 0, true), Is.True);
            Assert.That(AutonomousPetSupport.IsRequiredBonedancerArmyGap(1, 1, true), Is.False);
            Assert.That(AutonomousPetSupport.IsRequiredBonedancerArmyGap(1, 0, false), Is.False);
            Assert.That(AutonomousPetSupport.IsRequiredBonedancerArmyGap(0, 0, true), Is.False);
        });
    }

    [Test]
    public void RequiredBonedancerArmyGateIsExclusiveToTemporaryCompanions()
    {
        int bonedancer = (int)eCharacterClass.Bonedancer;
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousPetSupport.IsTemporaryCompanionBonedancerScope(true, false, bonedancer), Is.True);
            Assert.That(AutonomousPetSupport.IsTemporaryCompanionBonedancerScope(false, false, bonedancer), Is.False);
            Assert.That(AutonomousPetSupport.IsTemporaryCompanionBonedancerScope(true, true, bonedancer), Is.False);
            Assert.That(AutonomousPetSupport.IsTemporaryCompanionBonedancerScope(
                true, false, (int)eCharacterClass.Spiritmaster), Is.False);
        });
    }

    [Test]
    public void ClassicTheurgistLimitUsesNaturalCastingConstraints()
    {
        Assert.That(SummonTheurgistPet.PetLimitAllowsAnother(30, 0), Is.True);
        Assert.That(SummonTheurgistPet.PetLimitAllowsAnother(15, 16), Is.True);
        Assert.That(SummonTheurgistPet.PetLimitAllowsAnother(16, 16), Is.False);
    }

    [Test]
    public void GroupDefenseIsLocalRatherThanMapWide()
    {
        Assert.That(BotBrain.GROUP_DEFENSE_ASSIST_RADIUS, Is.EqualTo(2000));
    }

    [TestCase(eCharacterClass.Bard, true)]
    [TestCase(eCharacterClass.Minstrel, true)]
    [TestCase(eCharacterClass.Skald, true)]
    [TestCase(eCharacterClass.Druid, false)]
    [TestCase(eCharacterClass.Cleric, false)]
    public void ClassicSongRotationIsRestrictedToOriginalSongClasses(
        eCharacterClass characterClass,
        bool expected)
    {
        Assert.That(BotBrain.IsClassicSongClass(characterClass), Is.EqualTo(expected));
    }

    [Test]
    public void PersistentSpecializationChoiceIsStableAndClassValid()
    {
        eSpecType first = BotSpec.ChoosePersistentSpecialization(eCharacterClass.Bonedancer, 183);
        eSpecType second = BotSpec.ChoosePersistentSpecialization(eCharacterClass.Bonedancer, 183);

        Assert.That(second, Is.EqualTo(first));
        Assert.That(BotSpec.GetSpecializationChoices(eCharacterClass.Bonedancer), Does.Contain(first));
        Assert.That(BotSpec.GetSpecializationChoices(eCharacterClass.Bonedancer).Distinct().Count(), Is.EqualTo(3));
    }

    [TestCase(eSpecType.DarkBone, Specs.Darkness)]
    [TestCase(eSpecType.SuppBone, Specs.Suppression)]
    [TestCase(eSpecType.ArmyBone, Specs.BoneArmy)]
    public void BonedancerPlanFocusesPrimaryThenOneStableSecondary(eSpecType primary, string primaryLine)
    {
        BonedancerBotSpec first = new(primary, 183, true);
        BonedancerBotSpec second = new(primary, 183, true);

        Assert.Multiple(() =>
        {
            Assert.That(first.SpecLines, Has.Count.EqualTo(2));
            Assert.That(first.SpecLines.Count(line => line.Spec == primaryLine && line.levelRatio >= 1.0f && line.SpecCap == 50), Is.EqualTo(1));
            Assert.That(first.SpecLines.Count(line => line.Spec != primaryLine && line.levelRatio == 0.0f && line.SpecCap == 50), Is.EqualTo(1));
            Assert.That(second.SpecLines.Select(line => line.Spec), Is.EqualTo(first.SpecLines.Select(line => line.Spec)));
        });
    }

    [Test]
    public void BonedancerPreferenceMatchesRoleWithoutForbiddingOtherRoles()
    {
        Spell caster = MinionSpell(BdSubPet.SubPetType.Caster);
        Spell healer = MinionSpell(BdSubPet.SubPetType.Healer);
        Spell melee = MinionSpell(BdSubPet.SubPetType.Melee);

        Assert.That(AutonomousPetSupport.IsPreferredBonedancerSubPet(eSpecType.DarkBone, caster), Is.True);
        Assert.That(AutonomousPetSupport.IsPreferredBonedancerSubPet(eSpecType.SuppBone, healer), Is.True);
        Assert.That(AutonomousPetSupport.IsPreferredBonedancerSubPet(eSpecType.ArmyBone, melee), Is.True);
        Assert.That(AutonomousPetSupport.IsPreferredBonedancerSubPet(eSpecType.SuppBone, caster), Is.False);
    }

    [TestCase(0, 0.10, 0)]
    [TestCase(1, 0.10, 1)]
    [TestCase(2, 0.90, 2)]
    [TestCase(3, 0.10, 2)]
    [TestCase(3, 0.90, 3)]
    [TestCase(5, 0.90, 3)]
    public void BonedancerCommanderUsesClassicSlotPlan(int capacity, double roll, int expected)
    {
        Assert.That(AutonomousPetSupport.BonedancerDesiredMinionCount(capacity, roll), Is.EqualTo(expected));
    }

    [TestCase(19, 15, 14)]
    [TestCase(30, 45, 22)]
    [TestCase(45, 45, 33)]
    [TestCase(50, 45, 37)]
    public void BonedancerSubPetLevelUsesCasterRatioAndSpellCap(int ownerLevel, int cap, int expected)
    {
        Spell spell = MinionSpell(BdSubPet.SubPetType.Melee);
        spell.Damage = -75;
        spell.Value = cap;
        Assert.That(AutonomousPetSupport.BonedancerSubPetLevel(ownerLevel, spell), Is.EqualTo(expected));
    }

    private static Spell MinionSpell(BdSubPet.SubPetType type) => new(new DbSpell
    {
        SpellID = 900000 + (int)type,
        Name = $"Test {type}",
        Target = eSpellTarget.SELF.ToString(),
        Type = eSpellType.SummonMinion.ToString(),
        DamageType = (int)type,
    }, 1);
}
