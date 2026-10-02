using System.Runtime.CompilerServices;
using System.Collections;
using System.Reflection;
using DOL.GS;
using DOL.GS.Commands;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_TeleportToExchange
    {
        private sealed class TestRegion : Region
        {
            private TestRegion() : base(default) { }
            public ushort RegionId;
            public override ushort ID => RegionId;
        }

        [TestCase(eRealm.Albion, 10)]
        [TestCase(eRealm.Midgard, 101)]
        [TestCase(eRealm.Hibernia, 201)]
        public void SelectsOnlyLiveSameRealmCapitalBroker(eRealm realm, int id)
        {
            RealmExchangeBroker broker = Broker(realm, (ushort)id);
            RealmExchangeBroker wrongRealm = Broker(realm == eRealm.Albion ? eRealm.Hibernia : eRealm.Albion, (ushort)id);
            RealmExchangeBroker wrongRegion = Broker(realm, 1);
            RealmExchangeBroker inactive = Broker(realm, (ushort)id);
            inactive.ObjectState = GameObject.eObjectState.Inactive;
            GameNPC merchant = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
            GameNPC[] npcs = { merchant, wrongRealm, wrongRegion, inactive, broker };
            Assert.That(TeleportToExchangeCommandHandler.CapitalRegion(realm), Is.EqualTo(id));
            Assert.That(TeleportToExchangeCommandHandler.FindBroker(realm, npcs), Is.SameAs(broker));
            broker.X = 12345; broker.Y = 23456; broker.Z = 8000;
            broker.movementComponent.ForceUpdatePosition();
            Assert.That(TeleportToExchangeCommandHandler.FindBroker(realm, npcs).X, Is.EqualTo(12345),
                "uses current NPC position, not stale saved coordinates");
        }

        [Test] public void MissingBrokerOrNonPlayerRealmDoesNotPickAnotherRealmsNpc()
        {
            Assert.That(TeleportToExchangeCommandHandler.FindBroker(eRealm.None,
                new[] { Broker(eRealm.Albion, 10) }), Is.Null);
            Assert.That(TeleportToExchangeCommandHandler.FindBroker(eRealm.Midgard,
                new[] { Broker(eRealm.Albion, 10) }), Is.Null);
            Assert.That(TeleportToExchangeCommandHandler.FindBroker(eRealm.Hibernia, null), Is.Null);
        }

        private static RealmExchangeBroker Broker(eRealm realm, ushort regionId)
        {
            var region = (TestRegion)RuntimeHelpers.GetUninitializedObject(typeof(TestRegion));
            region.RegionId = regionId;
            var broker = (RealmExchangeBroker)RuntimeHelpers.GetUninitializedObject(typeof(RealmExchangeBroker));
            typeof(GameNPC).GetField("m_brains", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(broker, new ArrayList());
            broker.Realm = realm;
            broker.CurrentRegion = region;
            broker.movementComponent = new NpcMovementComponent(broker);
            broker.ObjectState = GameObject.eObjectState.Active;
            return broker;
        }
    }
}
