using System.Collections.Generic;
using System.Linq;

namespace DOL.GS.Commands
{
    [CmdAttribute("&tc", ePrivLevel.Player, "Teleport to your capital's Realm Exchange", "/tc")]
    public sealed class TeleportToExchangeCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public static ushort CapitalRegion(eRealm realm) => realm switch
        {
            eRealm.Albion => 10,
            eRealm.Midgard => 101,
            eRealm.Hibernia => 201,
            _ => 0,
        };

        public static RealmExchangeBroker FindBroker(eRealm realm, IEnumerable<GameNPC> npcs)
        {
            ushort capital = CapitalRegion(realm);
            if (capital == 0 || npcs == null) return null;
            return npcs.OfType<RealmExchangeBroker>().FirstOrDefault(broker => broker.Realm == realm &&
                broker.CurrentRegionID == capital && broker.ObjectState == GameObject.eObjectState.Active);
        }

        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client?.Player;
            if (player == null) return;
            if (args.Length != 1)
            {
                DisplayMessage(client, "Usage: /tc (your current realm's capital exchange)");
                return;
            }
            if (!player.IsAlive || player.Steed != null || player.IsOnHorse || AutonomousPlayerPilot.IsActive(player))
            {
                DisplayMessage(client, "You must be alive and off a horse route before using /tc.");
                return;
            }

            Region capital = WorldMgr.GetRegion(CapitalRegion(player.Realm));
            RealmExchangeBroker broker = FindBroker(player.Realm, capital?.Objects.OfType<GameNPC>());
            if (broker == null)
            {
                DisplayMessage(client, "Your capital's Realm Exchange NPC is not currently available.");
                return;
            }

            PlayerMobNavigator.Stop(player, string.Empty, false);
            if (PlayerCompanionGrind.IsActive(player)) PlayerCompanionGrind.Stop(player, "you used /tc");
            // Use the real NPC's verified floor position, not a copied coordinate
            // or blind offset into nearby tables/walls. Native MoveTo retains
            // the existing companion-transfer hooks. No NPC/navmesh is changed.
            if (!player.MoveTo(broker.CurrentRegionID, broker.X, broker.Y, broker.Z, broker.Heading))
            {
                DisplayMessage(client, "Could not teleport to your Realm Exchange.");
                return;
            }
            DisplayMessage(client, $"Teleported to {broker.Name}, Realm Exchange in {capital.Description}.");
        }
    }
}
