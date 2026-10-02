using System.Net.Sockets;
using DOL.Database;

namespace DOL.GS
{
    public class BotDummyClient : GameClient
    {
        public BotDummyClient() : base((Socket)null)
        {
            Account = new DbAccount();
            Account.Language = "EN";
            Account.PrivLevel = (int)ePrivLevel.Player;
        }
    }
}
