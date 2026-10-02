using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;

namespace DOL.UnitTests
{
    // GameObject initializes its quest cache on first use. Isolated epic tests
    // need the same empty database as a full-suite run, without starting a server.
    internal sealed class EpicTestServerScope : IDisposable
    {
        private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private readonly GameServer _previous = GameServer.Instance;
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => Empty;
            protected override GS.ServerRules.IServerRules ServerRulesImpl => new GS.ServerRules.NormalServerRules();
        }
        public EpicTestServerScope() => GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        public void Dispose() => GameServer.LoadTestDouble(_previous);
    }
}
