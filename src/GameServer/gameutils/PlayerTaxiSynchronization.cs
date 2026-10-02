using System;
using System.Runtime.CompilerServices;
using DOL.Logging;

namespace DOL.GS
{
    // Human-client synchronization only. Never move or restart the horse route.
    public static class PlayerTaxiSynchronization
    {
        private sealed class State { public long NextCorrection; public long NextLog; }
        private static readonly ConditionalWeakTable<GamePlayer, State> States = new();
        private static readonly Logger Log = LoggerManager.Create(typeof(PlayerTaxiSynchronization));

        public static bool IsPlayerTaxiType(Type type) => type != null &&
            typeof(GameTaxi).IsAssignableFrom(type) && !typeof(GameBotTaxi).IsAssignableFrom(type);

        public static bool IsPlayerTaxi(GamePlayer player) => IsPlayerTaxiType(player?.Steed?.GetType());

        public static bool NeedsCorrection(float x, float y, float z, int horseX, int horseY, int horseZ)
        {
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return true;
            double dx = (double)x - horseX, dy = (double)y - horseY, dz = (double)z - horseZ;
            return dx * dx + dy * dy + dz * dz > 1000.0 * 1000;
        }

        public static void Correct(GamePlayer player, bool relocate)
        {
            if (!IsPlayerTaxi(player)) return;
            GameNPC horse = player.Steed;
            if (horse.ObjectState != GameObject.eObjectState.Active || horse.RiderSlot(player) < 0) return;
            State state = States.GetOrCreateValue(player);
            long now = GameLoop.GameLoopTime;
            if (now < state.NextCorrection) return;
            state.NextCorrection = now + 1000;
            // Keep mount-position, correction and attachment packets ordered on TCP.
            player.Out.SendObjectUpdate(horse, false);
            if (relocate)
            {
                player.X = horse.X; player.Y = horse.Y; player.Z = horse.Z; player.Heading = horse.Heading;
                player.Out.SendPlayerJump(false);
            }
            player.Out.SendRiding(player, horse, false);
            if (now >= state.NextLog)
            {
                state.NextLog = now + 30000;
                Log.Info($"PLAYER_TAXI_SYNC player={player.Name} horse={horse.ObjectID} relocate={relocate} region={horse.CurrentRegionID} position={horse.X},{horse.Y},{horse.Z}");
            }
        }

        public static void RecordDismountRequest(GamePlayer player)
        {
            if (!IsPlayerTaxi(player)) return;
            State state = States.GetOrCreateValue(player);
            // One entry per actual detachment; caller still honors voluntary dismount.
            Log.Info($"PLAYER_TAXI_DISMOUNT_REQUEST player={player.Name} horse={player.Steed.ObjectID} position={player.X},{player.Y},{player.Z} correctionRecently={GameLoop.GameLoopTime < state.NextCorrection + 2000}");
        }
    }
}
