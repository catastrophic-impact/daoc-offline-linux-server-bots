using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.PropertyCalc;
using DOL.Logging;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture, NonParallelizable]
public sealed class UT_BonedancerPetEquipment
{
    private sealed class EmptyServer : GameServer
    {
        protected override IObjectDatabase DataBaseImpl =>
            DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        protected override GS.ServerRules.IServerRules ServerRulesImpl => new GS.ServerRules.NormalServerRules();
    }

    private GameServer _oldServer;
    private PetTestLanguageScope _language;
    private IPropertyCalculator _oldMaxHealthCalculator;

    [OneTimeSetUp]
    public void InitializeLogging()
    {
        LoggerManager.InitializeWithExplicitLibrary(null, LogLibrary.None);
    }

    [SetUp]
    public void SetUp()
    {
        _oldServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        IPropertyCalculator[] calculators = (IPropertyCalculator[])typeof(GameLiving)
            .GetField("m_propertyCalc", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        _oldMaxHealthCalculator = calculators[(int)eProperty.MaxHealth];
        calculators[(int)eProperty.MaxHealth] = new MaxHealthCalculator();
        _language = new PetTestLanguageScope();
    }

    [TearDown]
    public void TearDown()
    {
        _language.Dispose();
        ((IPropertyCalculator[])typeof(GameLiving)
            .GetField("m_propertyCalc", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))[(int)eProperty.MaxHealth] =
            _oldMaxHealthCalculator;
        GameServer.LoadTestDouble(_oldServer);
    }

    [Test]
    public void OneHandCommanderAlwaysHasSkullShieldAndTwoHandClearsIt()
    {
        CommanderPet commander = new CommanderPet(Template(50, "skeletal commander"));

        commander.CommanderSwitchWeapon(CommanderPet.eWeaponType.OneHandHammer, false);
        Assert.That(commander.Inventory.GetItem(eInventorySlot.RightHandWeapon)?.Model, Is.EqualTo(3466));
        Assert.That(commander.Inventory.GetItem(eInventorySlot.LeftHandWeapon)?.Model, Is.EqualTo(3554));

        commander.CommanderSwitchWeapon(CommanderPet.eWeaponType.TwoHandHammer, false);
        Assert.That(commander.Inventory.GetItem(eInventorySlot.TwoHandWeapon)?.Model, Is.EqualTo(3465));
        Assert.That(commander.Inventory.GetItem(eInventorySlot.LeftHandWeapon), Is.Null);

        commander.CommanderSwitchWeapon(CommanderPet.eWeaponType.OneHandSword, false);
        Assert.That(commander.Inventory.GetItem(eInventorySlot.RightHandWeapon)?.Model, Is.EqualTo(3463));
        Assert.That(commander.Inventory.GetItem(eInventorySlot.LeftHandWeapon)?.Model, Is.EqualTo(3554));
    }

    [Test]
    public void BonePatrollerAlwaysHasMaceAndRoundBuckler()
    {
        BdBufferSubPet patroller = new BdBufferSubPet(Template(60, "bone patroller"));
        patroller.InitializeActiveWeaponFromInventory();

        Assert.That(patroller.Inventory.GetItem(eInventorySlot.RightHandWeapon)?.Model, Is.EqualTo(3466));
        Assert.That(patroller.Inventory.GetItem(eInventorySlot.LeftHandWeapon)?.Model, Is.EqualTo(1045));
        Assert.That(patroller.ActiveWeaponSlot, Is.EqualTo(eActiveWeaponSlot.Standard));
    }

    [Test]
    public void BoneDebufferWieldsTheCommandersTwoHandedMaceOnly()
    {
        BdDebufferSubPet debuffer = new BdDebufferSubPet(Template(61, "bonecurser"));
        debuffer.Inventory = new GameNPCInventory(new GameNpcInventoryTemplate());
        debuffer.Inventory.AddItem(eInventorySlot.RightHandWeapon,
            GameInventoryItem.Create(CommanderPet.GetWeaponTemplate(CommanderPet.eWeaponType.OneHandSword)));
        debuffer.InitializeActiveWeaponFromInventory();

        Assert.That(debuffer.Inventory.GetItem(eInventorySlot.TwoHandWeapon)?.Model, Is.EqualTo(3465));
        Assert.That(debuffer.Inventory.GetItem(eInventorySlot.RightHandWeapon), Is.Null, "template bone sword removed");
        Assert.That(debuffer.ActiveWeaponSlot, Is.EqualTo(eActiveWeaponSlot.TwoHanded));
    }

    [Test]
    public void BoneDebufferMaceHitsHarderButSlowerWithTheSameDamagePerSecond()
    {
        BdDebufferSubPet debuffer = new BdDebufferSubPet(Template(61, "bonecurser"));
        debuffer.InitializeActiveWeaponFromInventory();
        BdMeleeSubPet melee = new BdMeleeSubPet(Template(85, "bonecracker"));
        melee.Inventory = new GameNPCInventory(new GameNpcInventoryTemplate());
        melee.Inventory.AddItem(eInventorySlot.RightHandWeapon,
            GameInventoryItem.Create(CommanderPet.GetWeaponTemplate(CommanderPet.eWeaponType.OneHandHammer)));
        melee.SwitchWeapon(eActiveWeaponSlot.Standard);

        MethodInfo npcDamage = typeof(AttackComponent).GetMethod("CalculateNpcDamage", BindingFlags.Instance | BindingFlags.NonPublic);
        (double f1, double f2) = (GS.ServerProperties.Properties.PVE_MOB_DAMAGE_F1, GS.ServerProperties.Properties.PVE_MOB_DAMAGE_F2);
        double twoHandHit, oneHandHit;
        try
        {
            GS.ServerProperties.Properties.PVE_MOB_DAMAGE_F1 = 150;
            GS.ServerProperties.Properties.PVE_MOB_DAMAGE_F2 = 300;
            twoHandHit = (double)npcDamage.Invoke(debuffer.attackComponent, new object[] { debuffer.ActiveWeapon });
            oneHandHit = (double)npcDamage.Invoke(melee.attackComponent, new object[] { melee.ActiveWeapon });
        }
        finally
        {
            (GS.ServerProperties.Properties.PVE_MOB_DAMAGE_F1, GS.ServerProperties.Properties.PVE_MOB_DAMAGE_F2) = (f1, f2);
        }
        int twoHandSpeed = AttackComponent.NpcWeaponSpeed(debuffer.ActiveWeapon);
        int oneHandSpeed = AttackComponent.NpcWeaponSpeed(melee.ActiveWeapon);

        Assert.That(twoHandSpeed, Is.GreaterThan(oneHandSpeed), "slower swing");
        Assert.That(twoHandHit, Is.GreaterThan(oneHandHit), "harder hit");
        Assert.That(twoHandHit / twoHandSpeed, Is.EqualTo(oneHandHit / oneHandSpeed).Within(1e-9), "same damage per second");
    }

    private static NpcTemplate Template(int id, string name) => new(new DbNpcTemplate
    {
        TemplateId = id,
        Name = name,
        Model = "995",
        Level = "15",
        Size = "50",
        MaxSpeed = 200,
        Strength = 60,
        Constitution = 60,
        Dexterity = 60,
        Quickness = 60,
    });
}
