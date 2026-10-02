using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.GS;
using DOL.Database;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
using DOL.AI.Brain;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [NonParallelizable]
    public class UT_NecromancerPetAppearance
    {
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previousServer;
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }
        [SetUp]
        public void SetUp()
        {
            _previousServer = GameServer.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }
        [TearDown]
        public void TearDown() => GameServer.LoadTestDouble(_previousServer);

        public class PacketRecorder : DispatchProxy
        {
            public readonly List<string> Calls = new();
            protected override object Invoke(MethodInfo method, object[] args)
            {
                Calls.Add(method.Name);
                return null;
            }
        }

        private class Owner : GamePlayer
        {
            private Owner() : base(null, null) { }
            public IPacketLib Packets;
            public override IPacketLib Out => Packets;
            public override void Shade(bool state) { }
            public override IControlledBrain ControlledBrain { get; set; }
        }

        [TestCase(204)]
        [TestCase(206)]
        public void OpeningOwnerPetWindowCompletesCosmeticEquipmentHandshake(int template)
        {
            using var language = new PetTestLanguageScope();
            var owner = (Owner)RuntimeHelpers.GetUninitializedObject(typeof(Owner));
            owner.Packets = DispatchProxy.Create<IPacketLib, PacketRecorder>();
            // Existing control avoids needing unrelated player array initialization.
            owner.ControlledBrain = new NecromancerPetBrain(owner);
            var pet = Pet(template);
            var brain = new NecromancerPetBrain(owner) { Body = pet };
            var characterClass = new ClassNecromancer();
            characterClass.Init(owner);
            characterClass.SetControlledBrain(brain);
            var calls = ((PacketRecorder)owner.Packets).Calls;
            Assert.That(calls, Is.EqualTo(new[] { "SendPetWindow", "SendNPCCreate", "SendLivingEquipmentUpdate" }));
            Assert.That(pet.Inventory, Is.Null, "Appearance must not add combat equipment");
        }

        private static NecromancerPet Pet(int template)
        {
            var pet = (NecromancerPet)RuntimeHelpers.GetUninitializedObject(typeof(NecromancerPet));
            typeof(NecromancerPet).GetField("<AppearanceTemplateId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(pet, template);
            return pet;
        }

        private class ProcTarget : GameNPC
        {
            public bool Alive = true;
            public override bool IsAlive => Alive;
        }

        private class ProcPet : NecromancerPet
        {
            private ProcPet() : base(null) { }
            public bool Roll;
            public int Applied;
            public double ObservedChance;
            public override bool Chance(RandomDeckEvent deckEvent, double chancePercent)
            {
                ObservedChance = chancePercent;
                return Roll;
            }
            protected override void ApplyHeroFireProc(AttackData ad) => Applied++;
        }

        [TestCase(206, eAttackResult.HitUnstyled, AttackData.eAttackType.MeleeOneHand, true, true, 1)]
        [TestCase(206, eAttackResult.HitStyle, AttackData.eAttackType.MeleeTwoHand, true, true, 1)]
        [TestCase(206, eAttackResult.HitUnstyled, AttackData.eAttackType.MeleeOneHand, false, true, 0)]
        [TestCase(206, eAttackResult.HitUnstyled, AttackData.eAttackType.MeleeOneHand, true, false, 0)]
        [TestCase(204, eAttackResult.HitUnstyled, AttackData.eAttackType.MeleeOneHand, true, true, 0)]
        [TestCase(206, eAttackResult.Missed, AttackData.eAttackType.MeleeOneHand, true, true, 0)]
        [TestCase(206, eAttackResult.Blocked, AttackData.eAttackType.MeleeOneHand, true, true, 0)]
        [TestCase(206, eAttackResult.HitUnstyled, AttackData.eAttackType.Spell, true, true, 0)]
        [TestCase(206, eAttackResult.HitUnstyled, AttackData.eAttackType.Ranged, true, true, 0)]
        public void FireProcOnlyRunsOnSuccessfulHeroMeleeHits(int template, eAttackResult result,
            AttackData.eAttackType type, bool roll, bool alive, int expected)
        {
            var pet = (ProcPet)RuntimeHelpers.GetUninitializedObject(typeof(ProcPet));
            typeof(NecromancerPet).GetField("<AppearanceTemplateId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(pet, template);
            pet.Roll = roll;
            var target = (ProcTarget)RuntimeHelpers.GetUninitializedObject(typeof(ProcTarget));
            target.Alive = alive;
            pet.CheckWeaponMagicalEffect(new AttackData { Attacker = pet, Target = target, AttackResult = result, AttackType = type });
            Assert.That(pet.Applied, Is.EqualTo(expected));
            Assert.That(pet.Inventory, Is.Null);
            if (expected == 1)
                Assert.That(pet.ObservedChance, Is.EqualTo(1.0 / 7.0).Within(0.000001));
        }

        [Test]
        public void HeroFlamesArePresentationOnly()
        {
            var pet = Pet(206);
            Assert.That(NecromancerPetAppearance.Equipment(pet).Single().Effect, Is.EqualTo(22));
            Assert.That(NecromancerPet.HeroFireProcDamage, Is.EqualTo(95));
            Assert.That(pet.Inventory, Is.Null);
        }

        [TestCase(204, 4822, 0x10)]
        [TestCase(206, 4808, 0x22)]
        public void VisualWeaponsDoNotBecomeCombatEquipment(int template, int model, int slots)
        {
            var pet = Pet(template);
            var before = pet.Inventory;
            var items = NecromancerPetAppearance.Equipment(pet);
            Assert.That(items.Any(item => item.Model == model), Is.True);
            Assert.That(NecromancerPetAppearance.WeaponSlots(pet), Is.EqualTo(slots));
            ushort weapon = 0, defense = 0;
            NecromancerPetAppearance.CombatModels(pet, null, 10, ref weapon, ref defense);
            Assert.That(weapon, Is.EqualTo(model));
            Assert.That(defense, Is.Zero);
            Assert.That(pet.Inventory, Is.SameAs(before));
            Assert.That(pet.ActiveWeapon, Is.Null);
        }

        [Test]
        public void ShieldAppearanceOnlyDecoratesAnExistingBlockResult()
        {
            var pet = Pet(204);
            ushort weapon = 7, defense = 0;
            NecromancerPetAppearance.CombatModels(null, pet, 10, ref weapon, ref defense);
            Assert.That(defense, Is.Zero);
            NecromancerPetAppearance.CombatModels(null, pet, 2, ref weapon, ref defense);
            Assert.That(defense, Is.EqualTo(4823));
            Assert.That(weapon, Is.EqualTo(7));
        }

        [Test]
        public void EarlierPetsKeepTheirOriginalAnimationModels()
        {
            ushort weapon = 6, defense = 8;
            NecromancerPetAppearance.CombatModels(Pet(203), Pet(203), 2, ref weapon, ref defense);
            Assert.That(weapon, Is.EqualTo(6));
            Assert.That(defense, Is.EqualTo(8));
        }
    }
}
