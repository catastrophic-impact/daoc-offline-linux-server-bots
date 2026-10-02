using System.Linq;

namespace DOL.GS.PacketHandler.Client.v168
{
    /// <summary>
    /// Handles the disband group packet
    /// </summary>
    [PacketHandlerAttribute(PacketHandlerType.TCP, eClientPackets.DisbandFromGroup, "Disband From Group Request Handler", eClientStatus.PlayerInGame)]
    public class DisbandFromGroupHandler : PacketHandler
    {
        protected override void HandlePacketInternal(GameClient client, GSPacketIn packet)
        {
            GamePlayer player = client.Player;

            if (player.Group == null)
                return;

            GameLiving disbandMember = player;

            if (player.TargetObject != null &&
                player.TargetObject is GameLiving livingTarget &&
                livingTarget.Group != null &&
                livingTarget.Group == player.Group)
            {
                disbandMember = livingTarget;
            }

            if (disbandMember != player && player != player.Group.Leader)
                return;

            if (disbandMember == player)
            {
                // Match the /disband command: remove temporary helpers owned
                // by this player so they cannot survive the player's leave and
                // continue following in the world.
                GameBot[] ownedHelpers = player.Group.GetMembersInTheGroup()
                    .OfType<GameBot>()
                    .Where(bot => bot.IsTemporaryGroupHelper && bot.Owner == player)
                    .ToArray();
                foreach (GameBot helper in ownedHelpers)
                    helper.Delete();
            }

            if (player.Group != null)
                player.Group.RemoveMember(disbandMember);
        }
    }
}
