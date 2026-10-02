using DOL.GS.PacketHandler;

namespace DOL.GS.Commands;

[CmdAttribute("&faction", ePrivLevel.Player,
    "Talk to players and active playerbots in your realm", "/faction <message>")]
public sealed class FactionChatCommand : AbstractCommandHandler, ICommandHandler
{
    private const string FactionChatTick = "Offline_Faction_Chat_Tick";

    public void OnCommand(GameClient client, string[] args)
    {
        if (args.Length < 2)
        {
            client.Out.SendMessage("Usage: /faction <message>", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }
        if (client.Player.IsMuted || !GameServer.ServerRules.IsAllowedToSpeak(client.Player, "faction"))
        {
            client.Out.SendMessage("You cannot use faction chat right now.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        long last = client.Player.TempProperties.GetProperty<long>(FactionChatTick);
        if (last > 0 && client.Player.CurrentRegion.Time - last < 900)
        {
            client.Out.SendMessage("Slow down! Think before sending another faction message.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }
        string message = string.Join(" ", args, 1, args.Length - 1).Trim();
        AutonomousBotChatCoordinator.OnPlayerFactionChat(client.Player, message);
        client.Player.TempProperties.SetProperty(FactionChatTick, client.Player.CurrentRegion.Time);
    }
}
