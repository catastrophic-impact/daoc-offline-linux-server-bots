using System;

namespace DOL.GS.Commands
{
    [CmdAttribute("&fly", ePrivLevel.GM, "Toggle invulnerable observer flight", "/fly — toggle observer flight; returns to your starting position when disabled")]
    public sealed class FlyCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        public const string ActiveKey = "Offline.ObserverFlight";
        // Installed monsters.csv: 666 = Invisible Warrior; 150 is a visible troll.
        public const ushort InvisibleModel = 666;
        private const string StateKey = "Offline.ObserverFlight.State";
        private sealed record FlightState(ushort Region, int X, int Y, int Z, ushort Heading, bool Stealthed, bool Debug, bool Flight);

        public void OnCommand(GameClient client, string[] args)
        {
            GamePlayer player = client?.Player;
            if (player == null) return;
            if (IsSpammingCommand(player, "OfflineFly")) return;
            FlightState previous = player.TempProperties.GetProperty<FlightState>(StateKey);
            if (previous != null)
            {
                // Return to the known ground position before removing immunity.
                player.MoveTo(previous.Region, previous.X, previous.Y, previous.Z, previous.Heading);
                player.TempProperties.RemoveProperty(ActiveKey);
                player.TempProperties.RemoveProperty(StateKey);
                player.TempProperties.SetProperty(GamePlayer.DEBUG_MODE_PROPERTY, previous.Debug);
                player.IsAllowedToFly = previous.Flight;
                player.Stealth(previous.Stealthed);
                client.Out.SendModelChange(player, player.Model);
                client.Out.SendObserverFlightMode(false, previous.Debug);
                DisplayMessage(client, "Observer flight OFF. Returned to your starting position.");
            }
            else
            {
                if (!player.IsAlive || player.InCombat || player.IsCasting || player.IsOnHorse)
                {
                    DisplayMessage(client, "Leave combat, finish casting and dismount before using /fly.");
                    return;
                }
                player.TempProperties.SetProperty(StateKey, new FlightState(player.CurrentRegionID, player.X, player.Y, player.Z,
                    player.Heading, player.IsStealthed, player.TempProperties.GetProperty<bool>(GamePlayer.DEBUG_MODE_PROPERTY), player.IsAllowedToFly));
                player.attackComponent.StopAttack();
                player.TargetObject = null;
                player.TempProperties.SetProperty(ActiveKey, true);
                player.TempProperties.SetProperty(GamePlayer.DEBUG_MODE_PROPERTY, true);
                player.IsAllowedToFly = true;
                player.Stealth(true);
                // Client-only model change: never save an invisible character model.
                client.Out.SendModelChange(player, InvisibleModel);
                client.Out.SendObserverFlightMode(true, true);
                DisplayMessage(client, "Observer flight ON. /fly again restores your view and returns you safely.");
            }
        }
    }
}
