using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[NonParallelizable]
public class UT_RealmExpeditionAttendance
{
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private sealed class Member : GameBot
    {
        private Member() : base((OfflineWorldBotRecord)null) { }
        public bool Alive = true;
        public Vector3 Position;
        public override bool IsAlive => Alive;
        public override bool InCombat => false;
        public override bool IsAttacking => false;
        public override byte Level { get => 50; set { } }
        public override ushort CurrentRegionID { get => 1; set { } }
        public override int X => (int)Position.X;
        public override int Y => (int)Position.Y;
        public override int Z => (int)Position.Z;
    }

    [Test]
    public void IndividualAttendanceLateReturnAndCorpseWaitDoNotRequireACompleteParty()
    {
        using var server = new EpicTestServerScope();
        Type manager = typeof(AutonomousRealmRaid);
        object raid = Activator.CreateInstance(manager.GetNestedType("Raid",BindingFlags.NonPublic),true);
        object party = Activator.CreateInstance(manager.GetNestedType("Party",BindingFlags.NonPublic),true);
        var hub = RealmRaidMuster.Hubs[0];
        var bots = Enumerable.Range(0,8).Select(i => {
            var b=(Member)RuntimeHelpers.GetUninitializedObject(typeof(Member));
            b.Alive=true; b.DatabaseID=900000+i; b.Position=hub.Center;
            b.ObjectState=GameObject.eObjectState.Active;
            typeof(GameNPC).GetField("m_brains",Hidden).SetValue(b,new ArrayList());
            typeof(GameBot).GetField("<IsAutonomousWorldBot>k__BackingField",Hidden).SetValue(b,true);
            return b;
        }).ToArray();
        var group=new Group(bots[0]);
        typeof(Group).GetField("_groupMembers",Hidden).SetValue(group,bots.Cast<GameLiving>().ToList());
        foreach(var b in bots) b.Group=group;
        void R(string field,object value)=>raid.GetType().GetField(field).SetValue(raid,value);
        void P(string field,object value)=>party.GetType().GetField(field).SetValue(party,value);
        var muster=new AutonomousRealmRaid.View("test-attendance","Muster",new("test","boss","hub",1,(int)hub.Center.X,(int)hub.Center.Y,(int)hub.Center.Z,false,false,50),true,true);
        var final=muster with { State="Final staging", Muster=false, Camp=muster.Camp with {X=muster.Camp.X+10000} };
        R("Hub",hub); R("Definition",AutonomousRealmRaid.Definitions[0]);
        P("Members",bots.Cast<GameBot>().ToArray()); P("HubPost",hub.Center); P("View",muster); P("DestinationView",final);
        ((IDictionary)raid.GetType().GetField("Parties").GetValue(raid)).Add(group,party);
        var membership=(IDictionary)manager.GetField("Membership",BindingFlags.NonPublic|BindingFlags.Static).GetValue(null);
        membership.Add(group,raid);
        try
        {
            bots[7].Position+=new Vector3(3000,0,0);
            int present=(int)manager.GetMethod("PresentAtHub",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{raid,party});
            Assert.That(present,Is.EqualTo(7),"One absent member cannot erase seven present members");
            Assert.That(AutonomousRealmRaid.Protects(bots[0]),Is.True);
            R("Created",GameLoop.GameLoopTime - 89 * 60_000L);
            Assert.That(AutonomousRealmRaid.Protects(bots[0]),Is.True,"Staging protection has no old sixty-minute cutoff");
            Assert.That(AutonomousRealmRaid.Protects(bots[7]),Is.False,"A stranded traveler must not borrow staging protection");
            R("HubDeparted",true);
            AutonomousRealmRaid.RejoinAfterRelease(bots[7]);
            Assert.That(AutonomousRealmRaid.GetTravelView(bots[7]),Is.SameAs(final));
            bots[7].Alive=false;
            Assert.That(AutonomousBotGroupCoordinator.PveCorpseRecovery(bots[7]),Is.EqualTo(AutonomousBotGroupCoordinator.PveCorpseDisposition.HoldForResurrection));
            var since=(Dictionary<long,long>)party.GetType().GetField("CorpseSince").GetValue(party);
            since[bots[7].DatabaseID]=GameLoop.GameLoopTime-90_001;
            Assert.That(AutonomousBotGroupCoordinator.PveCorpseRecovery(bots[7]),Is.EqualTo(AutonomousBotGroupCoordinator.PveCorpseDisposition.ReleaseAndRejoin));
            Assert.That(AutonomousRealmRaid.GetView(group),Is.Not.Null,"Corpse timeout must preserve expedition membership");
            Assert.That(group.MemberCount,Is.EqualTo(8));
        }
        finally { membership.Remove(group); }
    }
}
