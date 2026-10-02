using System;
using System.Linq;
using DOL.GS.PacketHandler;

namespace DOL.GS.Commands
{
    public static class PlayerBotTeleport
    {
        public static bool TryTeleport(GamePlayer player, GameBot bot, out string message)
        {
            if (player == null)
            {
                message = "No logged-in player character was found.";
                return false;
            }

            if (bot?.CurrentRegion == null || bot.ObjectState != GameObject.eObjectState.Active)
            {
                message = "The selected playerbot is no longer online.";
                return false;
            }

            if (!player.MoveTo(bot.CurrentRegionID, bot.X, bot.Y, bot.Z, bot.Heading))
            {
                message = $"Could not teleport to {bot.Name}'s current location.";
                return false;
            }

            message = $"Teleported to {bot.Name} in {bot.CurrentZone?.Description ?? bot.CurrentRegion.Description}.";
            return true;
        }
    }

    [CmdAttribute("&tele", ePrivLevel.Player, "Teleport to a playerbot or monster camp", "/tele <playerbot name>", "/tele mob <monster name>")]
    public sealed class TeleportToBotCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public void OnCommand(GameClient client, string[] args)
        {
            if (client?.Player == null || args.Length < 2)
            {
                DisplayMessage(client, "Usage: /tele <playerbot name> or /tele mob <monster name>");
                return;
            }

            if (args[1].Equals("mob", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 3)
                {
                    DisplayMessage(client, "Usage: /tele mob <monster name>");
                    return;
                }
                PlayerMobNavigator.TryTeleportToMonster(client.Player, string.Join(' ', args.Skip(2)).Trim(), out string message);
                DisplayMessage(client, message);
                return;
            }

            string botName = string.Join(' ', args.Skip(1)).Trim();
            if (!AutonomousBotRegistry.TryGetByName(botName, out GameBot bot))
            {
                DisplayMessage(client, $"No online persistent playerbot named '{botName}' was found.");
                return;
            }

            PlayerBotTeleport.TryTeleport(client.Player, bot, out string teleportMessage);
            DisplayMessage(client, teleportMessage);
        }
    }
}
