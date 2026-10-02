using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_TemporaryCompanionPetHealing
    {
        private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly IObjectDatabase EmptyDatabase =
            DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previousServer;

        private sealed class Rules : NormalServerRules { }
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
            protected override IServerRules ServerRulesImpl => new Rules();
        }

        [SetUp]
        public void SetUp()
        {
            _previousServer = GameServer.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }

        [TearDown]
        public void TearDown() => GameServer.LoadTestDouble(_previousServer);

        [Test]
        public void SupportedCompanionRequiresExactTemporaryPlayerGroup()
        {
            GamePlayer player = Actor<GamePlayer>();
            GameBot companion = Actor<GameBot>();
            Group group = PutInGroup(player, companion);
            ConfigureCompanion(companion, player, temporary: true);

            Assert.That(TemporaryCompanionPetHealing.TryGetSupportedPlayer(companion, out GamePlayer found), Is.True);
            Assert.That(found, Is.SameAs(player));

            Set(companion, "<IsTemporaryGroupHelper>k__BackingField", false);
            Assert.That(TemporaryCompanionPetHealing.TryGetSupportedPlayer(companion, out _), Is.False,
                "Persistent/autonomous player bots must never enter companion pet healing.");

            Set(companion, "<IsTemporaryGroupHelper>k__BackingField", true);
            group.GetType().GetField("_groupMembers", Hidden)?.SetValue(group, new List<GameLiving> { companion });
            Assert.That(TemporaryCompanionPetHealing.TryGetSupportedPlayer(companion, out _), Is.False,
                "The human must still be an actual member of the exact group.");
        }

        [Test]
        public void HumanMainPetAndRecursiveMinionAreAccepted()
        {
            GamePlayer player = Actor<GamePlayer>();
            GameNPC mainPet = Pet(player);
            GameNPC subMinion = Pet(mainPet);

            Assert.That(TemporaryCompanionPetHealing.IsDirectPlayerPet(mainPet, player), Is.True);
            Assert.That(TemporaryCompanionPetHealing.IsDirectPlayerPet(subMinion, player), Is.True,
                "Bonedancer-style subordinate ownership must reach the human through its commander.");
        }

        [Test]
        public void PetBuffScopeIncludesOnlyTheCastersRealPetTree()
        {
            GamePlayer player = Actor<GamePlayer>();
            GamePlayer otherPlayer = Actor<GamePlayer>();
            GameBot companion = Actor<GameBot>();
            ConfigureCompanion(companion, player, temporary: true);

            GameNPC commander = Pet(player);
            GameNPC subPet = Pet(commander);
            GameNPC companionPet = Pet(companion);
            GameNPC otherPlayerPet = Pet(otherPlayer);

            Assert.Multiple(() =>
            {
                Assert.That(ControlledPetBuffScope.IsOwnedPetTreeMember(player, commander), Is.True);
                Assert.That(ControlledPetBuffScope.IsOwnedPetTreeMember(player, subPet), Is.True,
                    "Bonedancer sub-pets remain inside their caster's army scope.");
                Assert.That(ControlledPetBuffScope.IsOwnedPetTreeMember(player, companion), Is.False,
                    "A companion GameBot is a player-character party member, not a pet buff target.");
                Assert.That(ControlledPetBuffScope.IsOwnedPetTreeMember(player, companionPet), Is.False,
                    "A companion's pet belongs to that companion, not the human caster's pet tree.");
                Assert.That(ControlledPetBuffScope.IsOwnedPetTreeMember(player, otherPlayerPet), Is.False);
            });
        }

        [Test]
        public void PlayerBotPetBuffScopeStillIncludesItsCommanderAndSubPets()
        {
            GameBot bonedancer = Actor<GameBot>();
            GameNPC commander = Pet(bonedancer);
            GameNPC subPet = Pet(commander);

            Assert.That(ControlledPetBuffScope.IsOwnedPetTreeMember(bonedancer, commander), Is.True);
            Assert.That(ControlledPetBuffScope.IsOwnedPetTreeMember(bonedancer, subPet), Is.True);
        }

        [Test]
        public void CollectorHelperStillIncludesAttachedHumanTree()
        {
            GamePlayer player = Actor<GamePlayer>();
            GameBot companion = Actor<GameBot>();
            ConfigureCompanion(companion, player, temporary: true);
            GameNPC mainPet = Pet(player);
            GameNPC subMinion = Pet(mainPet);
            GameNPC fieldPet = Pet(player); // Theurgist/Animist-style: not attached to ControlledBrain.
            GameNPC companionFieldPet = Pet(companion);
            IControlledBrain mainBrain = (IControlledBrain)mainPet.Brain;
            Set(typeof(GameLiving), mainPet, "m_controlledBrain", new[] { (IControlledBrain)subMinion.Brain });

            HashSet<GameNPC> found = TemporaryCompanionPetHealing.CollectOwnedPets(player, mainBrain,
                new[] { fieldPet, companionFieldPet });

            Assert.That(found, Does.Contain(mainPet));
            Assert.That(found, Does.Contain(subMinion));
            Assert.That(found, Does.Contain(fieldPet));
            Assert.That(found, Does.Not.Contain(companionFieldPet));
        }

        [Test]
        public void ExactGroupCompanionPetIsAcceptedAndForeignCompanionPetIsRejected()
        {
            GamePlayer player = Actor<GamePlayer>();
            GameBot healer = Actor<GameBot>();
            GameBot companion = Actor<GameBot>();
            GameBot foreign = Actor<GameBot>();
            PutInGroup(player, healer, companion);
            ConfigureCompanion(healer, player, temporary: true);
            ConfigureCompanion(companion, player, temporary: true);
            ConfigureCompanion(foreign, player, temporary: true);
            GameNPC companionPet = Pet(companion);
            GameNPC foreignPet = Pet(foreign);

            Assert.That(((IControlledBrain)companionPet.Brain).GetPlayerOwner(), Is.SameAs(player),
                "The native helper reaches the player through the companion.");
            Assert.That(TemporaryCompanionPetHealing.IsDirectPlayerPet(companionPet, player), Is.False);
            Assert.That(TemporaryCompanionPetHealing.IsPartyControlledPet(healer, companionPet, out bool playerOwned), Is.True);
            Assert.That(playerOwned, Is.False);
            Assert.That(TemporaryCompanionPetHealing.IsPartyControlledPet(healer, foreignPet, out _), Is.False);
            Assert.That(TemporaryCompanionPetHealing.IsDirectPlayerPet(companion, player), Is.False,
                "A GameBot is a party member, never a pet candidate, even though its brain is controlled-shaped.");
        }

        [Test]
        public void HealingExpansionKeepsAllExactPartyControlledPets()
        {
            GamePlayer player = Actor<GamePlayer>();
            GamePlayer otherPlayer = Actor<GamePlayer>();
            GameBot healer = Actor<GameBot>();
            GameBot groupCompanion = Actor<GameBot>();
            PutInGroup(player, healer, groupCompanion);
            ConfigureCompanion(healer, player, temporary: true);
            ConfigureCompanion(groupCompanion, player, temporary: true);

            GameNPC playerPet = Pet(player);
            GameNPC playerSubMinion = Pet(playerPet);
            GameNPC companionPet = Pet(groupCompanion);
            GameNPC otherPlayerPet = Pet(otherPlayer);
            GameNPC ordinaryNpc = Actor<GameNPC>();
            var targets = new List<GameLiving>
            {
                player, healer, groupCompanion, playerPet, playerSubMinion,
                companionPet, otherPlayerPet, ordinaryNpc
            };
            var heal = new Spell(new DbSpell { Type = "Heal", Target = "Realm", Value = 10 }, 1);

            TemporaryCompanionPetHealing.AdjustHealingTargets(healer, heal, targets, false);

            Assert.That(targets, Does.Contain(playerPet));
            Assert.That(targets, Does.Contain(playerSubMinion));
            Assert.That(targets, Does.Contain(companionPet));
            Assert.That(targets, Does.Not.Contain(otherPlayerPet));
            Assert.That(targets, Does.Contain(groupCompanion), "Companion party members remain healable.");
            Assert.That(targets, Does.Contain(ordinaryNpc), "Non-pet native targeting is not rewritten.");
        }

        [Test]
        public void DirectHealPriorityIsPlayerThenPlayerPetsThenCompanionsThenCompanionPets()
        {
            GamePlayer player = Actor<GamePlayer>();
            GameBot healer = Actor<GameBot>();
            GameBot companion = Actor<GameBot>();
            PutInGroup(player, healer, companion);
            ConfigureCompanion(healer, player, temporary: true);
            ConfigureCompanion(companion, player, temporary: true);
            GameNPC playerPet = Pet(player);
            GameNPC companionPet = Pet(companion);

            Assert.Multiple(() =>
            {
                Assert.That(TemporaryCompanionPetHealing.HealingPriority(healer, player), Is.EqualTo(0));
                Assert.That(TemporaryCompanionPetHealing.HealingPriority(healer, playerPet), Is.EqualTo(1));
                Assert.That(TemporaryCompanionPetHealing.HealingPriority(healer, companion), Is.EqualTo(2));
                Assert.That(TemporaryCompanionPetHealing.HealingPriority(healer, companionPet), Is.EqualTo(3));
            });
        }

        [Test]
        public void NonTemporaryCasterTargetExpansionIsUnchanged()
        {
            GamePlayer player = Actor<GamePlayer>();
            GameBot persistent = Actor<GameBot>();
            ConfigureCompanion(persistent, player, temporary: false);
            var heal = new Spell(new DbSpell { Type = "Heal", Target = "Realm", Value = 10 }, 1);
            GameNPC arbitraryPet = Pet(Actor<GamePlayer>());
            var targets = new List<GameLiving> { arbitraryPet };

            TemporaryCompanionPetHealing.AdjustHealingTargets(persistent, heal, targets, false);

            Assert.That(targets, Is.EqualTo(new[] { arbitraryPet }));
        }

        [TestCase("Self", false)]
        [TestCase("Pet", false)]
        [TestCase("Controlled", false)]
        [TestCase("Realm", true)]
        [TestCase("Group", true)]
        public void PartyPetBuffingNeverBroadensOwnPetOrSelfSpells(string target, bool expected)
        {
            var spell = new Spell(new DbSpell { Type = "StrengthBuff", Target = target, Value = 10 }, 1);
            Assert.That(BotGroupPetBuffTargets.IsShareable(spell), Is.EqualTo(expected));
        }

        [Test]
        public void ShareableBuffPetTreeKeepsOwnPetsAndIncludesBotAndPlayerPets()
        {
            foreach (GameLiving owner in new GameLiving[] { Actor<GamePlayer>(), Actor<GameBot>() })
            {
                GameNPC main = Pet(owner);
                GameNPC sub = Pet(main);
                Set(typeof(GameLiving), main, "m_controlledBrain", new[] { (IControlledBrain)sub.Brain, (IControlledBrain)sub.Brain });
                var found = new List<GameNPC>(BotGroupPetBuffTargets.AttachedTree(
                    (IControlledBrain)main.Brain, owner, new HashSet<IControlledBrain>()));
                Assert.That(found, Is.EqualTo(new[] { main, sub }),
                    "Own pets and subordinate pets remain eligible, without duplicate targets.");
                Assert.That(BotGroupPetBuffTargets.AttachedTree((IControlledBrain)main.Brain,
                    Actor<GamePlayer>(), new HashSet<IControlledBrain>()), Is.Empty,
                    "A stale or foreign pet pointer must not enter the owner's tree.");
            }
        }

        [Test]
        public void GroupPetBuffDepthMatchesNativeGroupSpellExpansion()
        {
            GamePlayer owner = Actor<GamePlayer>();
            GameNPC main = Pet(owner);
            GameNPC sub = Pet(main);
            GameNPC deeper = Pet(sub);
            Set(typeof(GameLiving), main, "m_controlledBrain", new[] { (IControlledBrain)sub.Brain });
            Set(typeof(GameLiving), sub, "m_controlledBrain", new[] { (IControlledBrain)deeper.Brain });
            Assert.That(BotGroupPetBuffTargets.AttachedTree((IControlledBrain)main.Brain,
                owner, new HashSet<IControlledBrain>(), 2), Is.EqualTo(new[] { main, sub }));
            Assert.That(BotGroupPetBuffTargets.AttachedTree((IControlledBrain)main.Brain,
                owner, new HashSet<IControlledBrain>()), Is.EqualTo(new[] { main, sub, deeper }));
        }

        private static GameNPC Pet(GameLiving owner)
        {
            GameNPC pet = Actor<GameNPC>();
            var brain = new ControlledMobBrain(owner) { Body = pet };
            Set(typeof(GameNPC), pet, "m_ownBrain", brain);
            return pet;
        }

        private static void ConfigureCompanion(GameBot bot, GamePlayer owner, bool temporary)
        {
            Set(bot, "<Owner>k__BackingField", owner);
            Set(bot, "<PlayerGroupLeader>k__BackingField", owner);
            Set(bot, "<IsTemporaryGroupHelper>k__BackingField", temporary);
        }

        private static Group PutInGroup(GamePlayer player, params GameBot[] bots)
        {
            var group = new Group(player);
            var members = (List<GameLiving>)group.GetType().GetField("_groupMembers", Hidden).GetValue(group);
            members.Add(player);
            player.Group = group;
            foreach (GameBot bot in bots)
            {
                members.Add(bot);
                bot.Group = group;
            }
            return group;
        }

        private static T Actor<T>() where T : GameLiving
        {
            T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            if (actor is GameNPC npc)
            {
                Set(typeof(GameNPC), npc, "m_brains", new ArrayList());
                Set(typeof(GameNPC), npc, "m_spells", new List<Spell>());
            }
            return actor;
        }

        private static void Set(object owner, string field, object value) =>
            Set(owner.GetType(), owner, field, value);

        private static void Set(Type declaringType, object owner, string field, object value) =>
            declaringType.GetField(field, Hidden)?.SetValue(owner, value);
    }
}
