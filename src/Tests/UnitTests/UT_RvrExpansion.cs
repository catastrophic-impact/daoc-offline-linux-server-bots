using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.Events;
using DOL.AI.Brain;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, NonParallelizable]
public sealed class UT_RvrExpansion
{
    private GameServer _previousServer;
    private object _previousTranslations;
    private string _previousLanguage;
    private sealed class InertServer : GameServer
    {
        protected override IObjectDatabase DataBaseImpl => DispatchProxy.Create<IObjectDatabase, DOL.UnitTests.UT_UnobservedConcentration.EmptyReads>();
        protected override DOL.GS.ServerRules.IServerRules ServerRulesImpl => new DOL.GS.ServerRules.NormalServerRules();
    }
    [SetUp]
    public void SetUp()
    {
        RvrEventTestState.Clear();
        _previousServer = GameServer.Instance;
        _previousLanguage = DOL.GS.ServerProperties.Properties.SERV_LANGUAGE;
        DOL.GS.ServerProperties.Properties.SERV_LANGUAGE = "EN";
        GameServer.LoadTestDouble((InertServer)RuntimeHelpers.GetUninitializedObject(typeof(InertServer)));
        var translations = typeof(DOL.Language.LanguageMgr).GetProperty("Translations");
        _previousTranslations = translations.GetValue(null);
        if (_previousTranslations == null) translations.SetValue(null, Activator.CreateInstance(translations.PropertyType));
    }
    [TearDown]
    public void TearDown()
    {
        RvrEventTestState.Clear();
        GameServer.LoadTestDouble(_previousServer);
        DOL.GS.ServerProperties.Properties.SERV_LANGUAGE = _previousLanguage;
        typeof(DOL.Language.LanguageMgr).GetProperty("Translations").SetValue(null, _previousTranslations);
    }
    [Test]
    public void ThreeThousandParticipantsRemainCappedWithIndependentRoamers()
    {
        string prefix = Guid.NewGuid().ToString();
        var objectives = new[]
        {
            new AutonomousRvrEventLayer.LiveObjective(prefix + "keep", "Keep", AutonomousRvrEventLayer.Intent.AssaultKeep,
                eRealm.Hibernia, 200, 100, 100, 0, false, 0, 0, 5, 2),
            new AutonomousRvrEventLayer.LiveObjective(prefix + "relic", "Relic keep", AutonomousRvrEventLayer.Intent.AssaultRelicKeep,
                eRealm.Midgard, 100, 100, 100, 0, true, 0, 0, 8, 3),
            new AutonomousRvrEventLayer.LiveObjective(prefix + "roam", "Roam", AutonomousRvrEventLayer.Intent.Roam,
                eRealm.None, 100, 1000, 1000, 0, false, 0, 0, 0, 0)
        };
        int reserved = 0;
        for (int i = 0; i < 375; i++)
        {
            bool reserve = i % 10 < 3;
            var force = new AutonomousRvrEventLayer.Force(prefix + i, (eRealm)(i % 3 + 1), 8, 50, 2, true, reserve);
            var plan = AutonomousRvrEventLayer.ChooseOrJoin(force, objectives, 100_000 + i, 0);
            if (reserve)
            {
                Assert.That(plan.IsSharedEvent, Is.False);
                reserved += force.MemberCount;
            }
        }
        Assert.That(reserved, Is.GreaterThanOrEqualTo(900));
        var battles = AutonomousRvrEventLayer.Snapshot().Where(b => b.TargetId.StartsWith(prefix)).ToArray();
        Assert.That(battles, Is.Not.Empty);
        foreach (var battle in battles)
            Assert.Multiple(() =>
            {
                Assert.That(battle.Attackers, Is.LessThanOrEqualTo(battle.CapPerRealm));
                Assert.That(battle.Defenders, Is.LessThanOrEqualTo(battle.CapPerRealm));
                Assert.That(battle.ThirdRealm, Is.LessThanOrEqualTo(battle.CapPerRealm));
            });
    }

    [TestCase(100, 0, false)]
    [TestCase(255, 0, false)]
    [TestCase(50, 0, true)]
    [TestCase(50, 99, true)]
    public void ProtectedPortalKeepsAreNotSiegeObjectivesButRelicKeepsRemainRaidable(int baseLevel, int skin, bool expected)
    {
        var keep = (DOL.GS.Keeps.GameKeep)RuntimeHelpers.GetUninitializedObject(typeof(DOL.GS.Keeps.GameKeep));
        keep.DBKeep = new DbKeep { BaseLevel = (byte)baseLevel, SkinType = (byte)skin, Realm = 2 };
        Assert.That(AutonomousRvrKeepPolicy.IsSiegeObjective(keep), Is.EqualTo(expected));
        Assert.That(keep.IsRelic, Is.EqualTo(skin == 99));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PortalKeepsCannotBeSelectedEvenWhenTheyAreTheOnlyObjective(bool reserve)
    {
        string id = Guid.NewGuid().ToString();
        var portal = new AutonomousRvrEventLayer.LiveObjective(id, "Protected portal", AutonomousRvrEventLayer.Intent.AssaultKeep,
            eRealm.Midgard, 100, 10, 10, 0, false, 0, 0, 1, 1, IsPortalKeep: true);
        var force = new AutonomousRvrEventLayer.Force(id, eRealm.Albion, 8, 50, 2, true, reserve);
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(force, [portal], GameLoop.GameLoopTime, 0), Is.Null);
        Assert.That(AutonomousRvrEventLayer.Snapshot().Any(b => b.TargetId == id), Is.False);
    }

    [Test]
    public void KeepAssaultStartsImmediatelyAndLateDefendersJoinUntilFourHourDeadline()
    {
        string id = Guid.NewGuid().ToString();
        long opened = GameLoop.GameLoopTime;
        var objective = new AutonomousRvrEventLayer.LiveObjective(id, "Persistent keep rally",
            AutonomousRvrEventLayer.Intent.AssaultKeep, eRealm.Hibernia, 200, 100, 100, 0, false, 0, 0, 5, 2);
        var attacker = new AutonomousRvrEventLayer.Force(id + "-attack", eRealm.Albion, 8, 50, 1);
        var defender = new AutonomousRvrEventLayer.Force(id + "-defend", eRealm.Hibernia, 8, 50, 1);

        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(attacker, [objective], opened, 0)?.TargetId, Is.EqualTo(id));
        long deadline = opened + AutonomousRvrEventLayer.Snapshot().Single().RemainingMilliseconds;
        var later = AutonomousRvrEventLayer.ChooseOrJoin(defender, [objective],
            deadline - 1, 0);

        Assert.Multiple(() =>
        {
            Assert.That(later?.TargetId, Is.EqualTo(id));
            Assert.That(AutonomousRvrEventLayer.GetRallyOrder(attacker.GroupId, eRealm.Albion, opened), Is.Null);
            Assert.That(AutonomousRvrEventLayer.IsBattleForce(attacker.GroupId, opened), Is.True);
            Assert.That(AutonomousRvrEventLayer.Snapshot().Any(battle => battle.TargetId == id), Is.True);
        });

        var reinforcement = AutonomousRvrEventLayer.ChooseOrJoin(
            new AutonomousRvrEventLayer.Force(id + "-late", eRealm.Midgard, 8, 50, 1), [objective with { UnderAttack = true }],
            deadline + 1, 0);
        Assert.Multiple(() =>
        {
            Assert.That(reinforcement?.TargetId, Is.Not.EqualTo(id), "Expired battle remains on cooldown.");
            Assert.That(AutonomousRvrEventLayer.IsBattleForce(attacker.GroupId,
                deadline + 1), Is.False);
        });
        AutonomousRvrEventLayer.EndTarget(id, deadline + 2);
    }

    [Test]
    public void NearFullRallyHasHigherJoinBiasThanEmptyRally()
    {
        var force = new AutonomousRvrEventLayer.Force("bias", eRealm.Albion, 8, 50, 1);
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRvrEventLayer.ShouldJoinActiveEvent(force, 0, 128, false, false, 0.65), Is.False);
            Assert.That(AutonomousRvrEventLayer.ShouldJoinActiveEvent(force, 112, 128, false, false, 0.65), Is.True);
        });
    }

    [TestCase(eRealm.Albion, eRealm.Midgard, eRealm.Hibernia)]
    [TestCase(eRealm.Midgard, eRealm.Hibernia, eRealm.Albion)]
    [TestCase(eRealm.Hibernia, eRealm.Albion, eRealm.Midgard)]
    public void DashboardNamesTheActualThirdRealm(eRealm attacker, eRealm defender, eRealm expected)
    {
        MethodInfo method = typeof(AutonomousRvrDashboard).GetMethod("ThirdRealm",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.That(method.Invoke(null, [attacker, defender]), Is.EqualTo(expected));
    }

    [Test]
    public void DashboardForceTextUsesTheActualThirdRealmName()
    {
        MethodInfo format = typeof(AutonomousRvrDashboard).GetMethod("FormatForces",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var battle = new AutonomousRvrEventLayer.BattleNotice("keep", "Keep siege rally",
            eRealm.Albion, eRealm.Midgard, 64, 48, 32, 128, 60_000,
            PresentAttackers: 64, PresentDefenders: 48, PresentThirdRealm: 32);
        string text = (string)format.Invoke(null, [battle])!;
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("Albion: 64"));
            Assert.That(text, Does.Contain("Midgard: 48"));
            Assert.That(text, Does.Contain("Hibernia: 32"));
            Assert.That(text, Does.Not.Contain("third realm").IgnoreCase);
        });
    }

    [Test]
    public void DashboardPublisherRegistersAndUnregistersFromCleanEventState()
    {
        FieldInfo instance = typeof(GameEventMgr).GetField("_soleInstance",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = (GameEventMgr)instance.GetValue(null)!;
        try
        {
            GameEventMgr.LoadTestDouble(new GameEventMgr());
            Assert.DoesNotThrow(() => AutonomousRvrDashboard.Start(null, null, EventArgs.Empty));
            Assert.DoesNotThrow(() => AutonomousRvrDashboard.Start(null, null, EventArgs.Empty),
                "Repeated startup must not duplicate or pre-remove handlers.");
            Assert.That(GameEventMgr.NumGlobalHandlers, Is.EqualTo(3));
            Assert.DoesNotThrow(() => AutonomousRvrDashboard.Stop(null, null, EventArgs.Empty));
            Assert.That(GameEventMgr.NumGlobalHandlers, Is.Zero);
        }
        finally
        {
            AutonomousRvrDashboard.Stop(null, null, EventArgs.Empty);
            GameEventMgr.LoadTestDouble(previous);
        }
    }

    [Test]
    public void KeepAndRelicEventsUseTheirIndependentRealmCaps()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRvrEventLayer.OrdinaryAssaultCap, Is.EqualTo(128));
            Assert.That(AutonomousRvrEventLayer.RelicAssaultCap, Is.EqualTo(192));
            Assert.That(AutonomousRvrEventLayer.RelicCarrierRealmCap, Is.EqualTo(192));
        });
    }

    [Test]
    public void NativeObjectiveResolutionImmediatelyReleasesCommittedForce()
    {
        string id = Guid.NewGuid().ToString();
        long now = GameLoop.GameLoopTime;
        var objective = new AutonomousRvrEventLayer.LiveObjective(id, "Captured keep",
            AutonomousRvrEventLayer.Intent.AssaultKeep, eRealm.Hibernia, 200, 100, 100, 0, false, 0, 0, 5, 2);
        var force = new AutonomousRvrEventLayer.Force(id + "-force", eRealm.Albion, 8, 50, 1);
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(force, [objective], now, 0)?.TargetId, Is.EqualTo(id));
        Assert.That(AutonomousRvrEventLayer.IsForceCommitted(force.GroupId, now), Is.True);

        AutonomousRvrEventLayer.EndTarget(id, now + 1);

        Assert.Multiple(() =>
        {
            Assert.That(AutonomousRvrEventLayer.IsTargetActive(id, now + 1), Is.False);
            Assert.That(AutonomousRvrEventLayer.IsForceCommitted(force.GroupId, now + 1), Is.False);
        });
    }

    [Test]
    public void CommittedSiegeForceCannotSwitchToAnotherRelicEvent()
    {
        string id = Guid.NewGuid().ToString();
        long now = GameLoop.GameLoopTime;
        var keep = new AutonomousRvrEventLayer.LiveObjective(id, "Locked keep",
            AutonomousRvrEventLayer.Intent.AssaultKeep, eRealm.Hibernia, 200, 100, 100, 0, false, 0, 0, 5, 2);
        var carrier = new AutonomousRvrEventLayer.LiveObjective(id + "-carrier", "Relic carrier",
            AutonomousRvrEventLayer.Intent.HuntEnemy, eRealm.Midgard, 100, 200, 200, 0, false, 1, 0, 0, 0, true);
        var force = new AutonomousRvrEventLayer.Force(id + "-force", eRealm.Albion, 8, 50, 1);
        Assert.That(AutonomousRvrEventLayer.ChooseOrJoin(force, [keep], now, 0)?.TargetId, Is.EqualTo(id));

        AutonomousRvrEventLayer.Plan retained = AutonomousRvrEventLayer.ChooseOrJoin(force, [keep, carrier], now + 1, 0);

        Assert.Multiple(() =>
        {
            Assert.That(retained?.TargetId, Is.EqualTo(id));
            Assert.That(retained?.IsSharedEvent, Is.True);
        });
    }

    [Test]
    public void DifferentWarbandsHaveDifferentFirstStagingCandidates()
    {
        AutonomousRvrStaging.TryGetBorderKeep(eRealm.Midgard, out var keep);
        Assert.That(AutonomousRvrStaging.CandidateAnchors(keep, 1).First(),
            Is.Not.EqualTo(AutonomousRvrStaging.CandidateAnchors(keep, 2).First()));
        Assert.That(AutonomousRvrStaging.CandidateAnchors(keep, 1).ToArray(),
            Is.EqualTo(AutonomousRvrStaging.CandidateAnchors(keep, 1).ToArray()));
    }

    private sealed class Carrier : GameBot
    {
        private Carrier() : base((OfflineWorldBotRecord)null) { }
        public override bool IsAlive => true;
        public override bool IsStealthed => false;
        public override eRealm Realm { get; set; }
        public override IControlledBrain ControlledBrain { get; set; }
        public override int X => RealX;
        public override int Y => RealY;
        public override int Z => RealZ;
    }

    private sealed class Human : GamePlayer
    {
        private Human() : base(null, null) { }
        public GameClient Connection;
        public override GameClient Client => Connection;
        public override bool IsAlive => true;
        public override bool IsInvulnerableToAttack => false;
        public override eRealm Realm { get; set; }
        public override IControlledBrain ControlledBrain { get; set; }
    }

    [Test]
    public void EnemyAutonomousBotExaminesAsEnemyFactionMember()
    {
        var viewer = (Human)RuntimeHelpers.GetUninitializedObject(typeof(Human));
        viewer.Realm = eRealm.Albion;
        var bot = (Carrier)RuntimeHelpers.GetUninitializedObject(typeof(Carrier));
        bot.Realm = eRealm.Midgard;
        bot.Name = "EnemyBot";
        typeof(GameBot).GetField("<IsAutonomousWorldBot>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(bot, true);

        Assert.That(bot.GetExamineMessages(viewer).Cast<string>().Single(),
            Does.Contain("member of an enemy faction"));
    }

    [Test]
    public void NativeRealmRulesAllowBothPlayerBotDirectionsAndAllEnemyBotRealms()
    {
        var human = (Human)RuntimeHelpers.GetUninitializedObject(typeof(Human));
        human.Connection = (GameClient)RuntimeHelpers.GetUninitializedObject(typeof(GameClient));
        human.Connection.Account = new DbAccount { Name = "test", PrivLevel = 1 };
        typeof(GameClient).GetField("<ClientState>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(human.Connection, GameClient.eClientState.Playing);
        var a = (Carrier)RuntimeHelpers.GetUninitializedObject(typeof(Carrier));
        var b = (Carrier)RuntimeHelpers.GetUninitializedObject(typeof(Carrier));
        foreach (var bot in new[] { a, b })
        {
            typeof(GameNPC).GetField("m_brains", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, new ArrayList());
            typeof(GameNPC).GetField("m_ownBrain", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, new BotBrain());
        }
        var rules = GameServer.ServerRules;
        foreach (eRealm first in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
        foreach (eRealm second in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
        {
            human.Realm = a.Realm = first; b.Realm = second;
            Assert.That(rules.IsAllowedToAttack(a, b, true), Is.EqualTo(first != second));
            Assert.That(rules.IsAllowedToAttack(human, b, true), Is.EqualTo(first != second));
            Assert.That(rules.IsAllowedToAttack(b, human, true), Is.EqualTo(first != second));
        }
        human.Connection.Account.PrivLevel = 2;
        human.Realm = eRealm.Albion; b.Realm = eRealm.Midgard;
        Assert.That(rules.IsAllowedToAttack(b, human, true), Is.False, "GM protection must not be stripped.");
    }

    private sealed class Relic : GameRelic
    {
        public int Saves;
        public override ushort CurrentRegionID { get => CurrentRegion?.ID ?? 0; set { } }
        public override void SaveIntoDatabase() => Saves++;
        public override bool AddToWorld() { ObjectState = eObjectState.Active; return true; }
        public override bool RemoveFromWorld() { ObjectState = eObjectState.Inactive; return true; }
        protected override void SetHandlers(GameLiving player, bool activate) { }
        protected override void StartPlayerTimer(GameLiving player) { }
    }

    [Test]
    public void RealRelicPickupWorksWithoutAPlayerAndCannotHaveTwoCarriers()
    {
        // Exercise native inventory/custody logic with only the world/timer/DB
        // adapters isolated. No live server, client, account or database writes.
        var region = new Region(new RegionData { Id = 100, Name = "Relic test", Description = "Relic test", Mobs = [] });
        var relic = new Relic { CurrentRegion = region, X = 1000, Y = 1000, Z = 100 };
        relic.AddToWorld();
        var item = new GameInventoryRelic(new DbItemTemplate { Id_nb = "GameRelic", Name = "Test relic", Model = 634, Level = 99 });
        typeof(GameRelic).GetField("_item", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(relic, item);
        Carrier Create()
        {
            var bot = (Carrier)RuntimeHelpers.GetUninitializedObject(typeof(Carrier));
            typeof(GameLiving).GetField("<TempProperties>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(bot, new PropertyCollection());
            typeof(GameBot).GetField("_dummyLib", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, new BotDummyPacketLib());
            typeof(GameNPC).GetField("m_brains", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, new ArrayList());
            foreach (string name in new[] { "_objectInRadiusCachesLock", "_objectsInRadiusCaches" })
            {
                var field = typeof(GameObject).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
                field.SetValue(bot, Activator.CreateInstance(field.FieldType));
            }
            bot.Inventory = new BotInventory();
            bot.movementComponent = new NpcMovementComponent(bot);
            bot.CurrentRegion = region;
            bot.X = 1000; bot.Y = 1000; bot.Z = 100;
            bot.Realm = eRealm.Midgard;
            bot.ObjectState = GameObject.eObjectState.Active;
            return bot;
        }
        var first = Create();
        var second = Create();
        Assert.That(relic.TryPickup(first), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(relic.CurrentCarrier, Is.SameAs(first));
            Assert.That(first.Inventory.AllItems, Does.Contain(item));
            Assert.That(item.CanPersist, Is.False);
            Assert.That(relic.TryPickup(second), Is.False);
            Assert.That(second.Inventory.AllItems, Is.Empty);
        });
        // Death/logout/recovery use this same native drop operation. Verify
        // exact location and custody while preserving unrelated real items.
        var equipment = new GameInventoryItem(new DbItemTemplate { Id_nb = "real-gear", Name = "Real gear" });
        Assert.That(first.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, equipment), Is.True);
        first.X = 1100;
        relic.DropFromCarrier(second); // A non-carrier cannot drop somebody else's relic.
        Assert.That(relic.CurrentCarrier, Is.SameAs(first));
        relic.DropFromCarrier(first);
        Assert.Multiple(() =>
        {
            Assert.That(relic.CurrentCarrier, Is.Null);
            Assert.That(relic.ObjectState, Is.EqualTo(GameObject.eObjectState.Active));
            Assert.That(relic.X, Is.EqualTo(1100));
            Assert.That(first.Inventory.AllItems, Does.Contain(equipment).And.Not.Contain(item));
        });
        second.X = 1100;
        second.Realm = eRealm.Albion;
        Assert.That(relic.TryPickup(second), Is.True, "An opposing realm can recover the dropped relic.");
        var wrongPad = new GameRelicPad { CurrentRegion = region, X = 1100, Y = 1000, Z = 100, Emblem = 2 };
        Assert.That(relic.RelicPadTakesOver(wrongPad, false), Is.False, "Wrong-realm shrine cannot claim it.");
        var homePad = new GameRelicPad { CurrentRegion = region, X = 1100, Y = 1000, Z = 100, Emblem = 1 };
        bool news = DOL.GS.ServerProperties.Properties.RECORD_NEWS;
        try
        {
            DOL.GS.ServerProperties.Properties.RECORD_NEWS = false;
            Assert.That(relic.RelicPadTakesOver(homePad, false), Is.True);
            Assert.That(relic.Realm, Is.EqualTo(eRealm.Albion));
            Assert.That(relic.CurrentCarrier, Is.Null);
            Assert.That(second.Inventory.AllItems, Is.Empty);
            Assert.That(homePad.MountedRelics, Does.Contain(relic));
        }
        finally { DOL.GS.ServerProperties.Properties.RECORD_NEWS = news; }
    }
}
