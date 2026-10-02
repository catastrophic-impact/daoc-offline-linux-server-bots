using System.Linq;
using DOL.AI.Brain;

namespace DOL.GS.Commands
{
    [CmdAttribute("&defensive", ePrivLevel.Player, "Companions wait for enemies to approach you", "/defensive")]
    public class CompanionDefensiveCommand : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            CompanionEngagementMode.Set(client.Player, true);
            if (client.Player.Group != null)
                foreach (GameBot bot in client.Player.Group.GetMembersInTheGroup().OfType<GameBot>())
                    if (CompanionEngagementMode.DefensiveLeader(bot) == client.Player && bot.Brain is BotBrain brain)
                        brain.EnforceCompanionEngagementRange();
            DisplayMessage(client, "Companions: DEFENSIVE. Pull enemies within 350 units of you; companions and their pets will engage nearby threats. Use /aggressive to restore normal assisting.");
        }
    }
    [CmdAttribute("&aggressive", ePrivLevel.Player, "Restore normal companion assisting (default)", "/aggressive")]
    public class CompanionAggressiveCommand : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            CompanionEngagementMode.Set(client.Player, false);
            DisplayMessage(client, "Companions: AGGRESSIVE (default). They assist your attacks normally again.");
        }
    }
}
