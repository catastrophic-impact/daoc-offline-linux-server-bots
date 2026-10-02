using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>Real combatants for epic encounter gates; not background NPCs or menu helpers.</summary>
    public static class EpicEncounterActors
    {
        public static bool IsCombatant(GameObject source) => source is GamePlayer or GameBot or GameSummonedPet ||
            source is GameNPC { Brain: IControlledBrain };

        public static bool CanTrigger(GameLiving actor) => actor?.IsAlive == true &&
            (actor is GamePlayer player && player.Client?.Account?.PrivLevel == 1 ||
             actor is GameBot bot && bot.Level == 50 && bot.Group != null &&
                (bot.IsAutonomousWorldBot || CompanionRaid.IsMember(bot)));

        public static IEnumerable<GameLiving> Nearby(GameNPC origin, ushort range) =>
            origin.GetPlayersInRadius(range).Cast<GameLiving>()
                .Concat(origin.GetNPCsInRadius(range).OfType<GameBot>()).Where(CanTrigger);
    }
}
