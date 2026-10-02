using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI;
using DOL.Database;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousWorldMoveLifecycle
{
    private sealed class TestServer : GameServer
    {
        public IObjectDatabase TestDatabase;
        protected override IObjectDatabase DataBaseImpl => TestDatabase;
    }

    private GameServer _previousServer;

    [SetUp]
    public void SetUp()
    {
        _previousServer = GameServer.Instance;
        TestServer server = (TestServer)RuntimeHelpers.GetUninitializedObject(typeof(TestServer));
        server.TestDatabase = DispatchProxy.Create<IObjectDatabase, DOL.UnitTests.UT_UnobservedConcentration.EmptyReads>();
        GameServer.LoadTestDouble(server);
    }

    [TearDown]
    public void TearDown() => GameServer.LoadTestDouble(_previousServer);

    [Test]
    public void NestedMoveScopeCannotClearOuterTransfer()
    {
        GameBot bot = (GameBot)RuntimeHelpers.GetUninitializedObject(typeof(GameBot));
        MethodInfo begin = typeof(GameBot).GetMethod("BeginIntentionalWorldMove", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo end = typeof(GameBot).GetMethod("EndIntentionalWorldMove", BindingFlags.Instance | BindingFlags.NonPublic);
        PropertyInfo active = typeof(GameBot).GetProperty("IsIntentionalWorldMove", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(begin, Is.Not.Null);
        Assert.That(end, Is.Not.Null);
        Assert.That(active, Is.Not.Null);

        begin.Invoke(bot, null);
        begin.Invoke(bot, null);
        Assert.That(active.GetValue(bot), Is.True);

        Assert.That(end.Invoke(bot, null), Is.False, "The nested move must not end the outer transfer scope");
        Assert.That(active.GetValue(bot), Is.True);

        Assert.That(end.Invoke(bot, null), Is.True);
        Assert.That(active.GetValue(bot), Is.False);
    }

    [Test]
    public void PatchedTransferMethodsLoadAndJit()
    {
        MethodInfo moveTo = typeof(GameBot).GetMethod(nameof(GameBot.MoveTo),
            [typeof(ushort), typeof(int), typeof(int), typeof(int), typeof(ushort)]);
        MethodInfo remove = typeof(GameBot).GetMethod(nameof(GameBot.RemoveFromWorld), Type.EmptyTypes);
        MethodInfo stopBrain = typeof(ABrain).GetMethod(nameof(ABrain.Stop), Type.EmptyTypes);

        Assert.That(moveTo, Is.Not.Null);
        Assert.That(remove, Is.Not.Null);
        Assert.That(stopBrain, Is.Not.Null);
        Assert.DoesNotThrow(() => RuntimeHelpers.PrepareMethod(moveTo.MethodHandle));
        Assert.DoesNotThrow(() => RuntimeHelpers.PrepareMethod(remove.MethodHandle));
        Assert.DoesNotThrow(() => RuntimeHelpers.PrepareMethod(stopBrain.MethodHandle));
    }
}
