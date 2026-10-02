using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture, NonParallelizable]
public class UT_SpiritmasterInterceptOwner
{
    private static readonly IObjectDatabase EmptyDatabase =
        System.Reflection.DispatchProxy.Create<IObjectDatabase, DOL.UnitTests.UT_UnobservedConcentration.EmptyReads>();
    private GameServer _previousServer;

    private sealed class InertServer : GameServer
    {
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
    }

    [SetUp]
    public void SetUp()
    {
        _previousServer = GameServer.Instance;
        GameServer.LoadTestDouble((InertServer)RuntimeHelpers.GetUninitializedObject(typeof(InertServer)));
    }

    [TearDown]
    public void TearDown() => GameServer.LoadTestDouble(_previousServer);

    private sealed class PetBrain : ControlledMobBrain
    {
        public PetBrain(GameLiving owner) : base(owner) { }

        public GameLiving ResolvedInterceptOwner => GetInterceptOwner();
    }

    [Test]
    public void CompanionPet_InterceptsForImmediateGameBotNotHumanCreator()
    {
        var companion = Bot();
        var brain = new PetBrain(companion);

        Assert.That(brain.ResolvedInterceptOwner, Is.SameAs(companion));
    }

    [Test]
    public void AutonomousPet_InterceptsForImmediateGameBotWithoutHumanOwner()
    {
        var autonomous = Bot();
        var brain = new PetBrain(autonomous);

        Assert.That(brain.ResolvedInterceptOwner, Is.SameAs(autonomous));
    }

    [Test]
    public void RealPlayerPet_StillInterceptsForRealPlayerOwner()
    {
        var player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        var brain = new PetBrain(player);

        Assert.That(brain.ResolvedInterceptOwner, Is.SameAs(player));
    }

    private static GameBot Bot()
    {
        var bot = (GameBot)RuntimeHelpers.GetUninitializedObject(typeof(GameBot));
        typeof(GameNPC).GetField("m_brains", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(bot, new ArrayList(1));
        return bot;
    }
}
