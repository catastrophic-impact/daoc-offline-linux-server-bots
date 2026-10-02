using DOL.GS.PacketHandler;
using DOL.GS.Quests;

namespace DOL.GS.Commands;

[CmdAttribute(
    "&bountylocation",
    ePrivLevel.Player,
    "Show the location of your active bounty target.",
    "/bountylocation")]
public sealed class BountyLocationCommand : AbstractCommandHandler, ICommandHandler
{
    public void OnCommand(GameClient client, string[] args)
    {
        GamePlayer player = client?.Player;
        if (player == null || IsSpammingCommand(player, "bountylocation"))
            return;

        if (player.IsDoingQuest(typeof(BountyQuest)) is not BountyQuest bounty)
        {
            player.Out.SendMessage("You have no active bounty location to show.",
                eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        // Re-create the marker from the saved quest if login/zone transition
        // disposed the transient map entry, then resend it on demand.
        bounty.ShowMarker();
        if (!BountyMapMarkers.Show(player, out BountyMapMarkers.Target target))
        {
            player.Out.SendMessage("This bounty has no saved map location. Ask the Bounty Master for another target.",
                eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return;
        }

        Zone zone = WorldMgr.GetRegion(target.Region)?.GetZone(target.X, target.Y);
        string area = zone?.Description ?? "the marked region";
        bool inTargetZone = player.CurrentRegionID == target.Region &&
                            zone != null && player.CurrentZone?.ID == zone.ID;
        string location = inTargetZone
            ? $"Red bounty marker refreshed. Open this zone's map (or BOUNTY MAP in your journal) to see it in {area}."
            : $"Travel to {area}, then open its local map (or BOUNTY MAP in your journal) to see the red bounty marker. It is not visible from another zone.";
        player.Out.SendMessage(location, eChatType.CT_System, eChatLoc.CL_SystemWindow);
    }
}
