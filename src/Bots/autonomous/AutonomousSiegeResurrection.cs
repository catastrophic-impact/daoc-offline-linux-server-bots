using System.Collections.Concurrent;

namespace DOL.GS
{
    /// <summary>
    /// A siege army raises its own dead, as players did: any living healer of the attacking realm near a fallen
    /// comrade raises it, not only the healer of its own eight. Run 21 (2026-10-17): Midgard won the fighting at Dun
    /// Dagda 214 kills to 115, but a dead attacker released at the realm's frontier muster about 160,000 units away
    /// (median of 115 bots) and the army at the keep fell from 125 to none while the party healers lay dead or stood
    /// elsewhere. A dead attacker with such a healer near it waits for the raise (GameBot's siege corpse wait).
    /// </summary>
    public static class AutonomousSiegeResurrection
    {
        public const int RaiseRadius = 2_000;
        public const long ClaimMilliseconds = 20_000;
        /// <summary>A healer in the battle raises only after this long without being hit.</summary>
        public const long QuietMilliseconds = 5_000;
        /// <summary>
        /// During the battle, a corpse with a living enemy this close is left for later. 500 kept every corpse at Dun Dagda's
        /// Relic Gate unraised (the defenders hold the gate the attackers die at; run 24: 0 corpses claimed).
        /// </summary>
        public const int EnemyClearRadius = 250;

        private static readonly ConcurrentDictionary<GameBot, (GameBot Healer, long Until)> Claims = new();

        /// <summary>The bot belongs to the attacking army of a running siege.</summary>
        public static bool IsSiegeAttacker(GameBot bot) =>
            bot is { IsAutonomousWorldBot: true } &&
            AutonomousRvrEventLayer.TrySiegeRelease(AutonomousBotGroupCoordinator.RvrForceId(bot), bot.Realm, GameLoop.GameLoopTime, out _, out _);

        public static bool CanRaise(GameBot healer) =>
            healer is { IsAlive: true, IsAutonomousWorldBot: true } && healer.ResurrectionSpell is { } spell &&
            healer.Mana >= healer.PowerCost(spell);

        /// <summary>A living healer of the corpse's realm stands within <see cref="RaiseRadius"/>.</summary>
        public static bool HasArmyResurrectorNear(GameBot corpse)
        {
            if (corpse == null || corpse.ObjectState != GameObject.eObjectState.Active) return false;
            foreach (GameNPC npc in corpse.GetNPCsInRadius(RaiseRadius))
                if (npc is GameBot healer && healer != corpse && healer.Realm == corpse.Realm && CanRaise(healer))
                    return true;
            return false;
        }

        /// <summary>The nearest fallen realm-mate within <see cref="RaiseRadius"/> that no other healer is raising.</summary>
        public static GameBot ClaimCorpse(GameBot healer, bool fighting = false)
        {
            long now = GameLoop.GameLoopTime;
            GameBot best = null;
            int bestDistance = int.MaxValue;
            foreach (GameNPC npc in healer.GetNPCsInRadius(RaiseRadius))
            {
                if (npc is not GameBot dead || dead.IsAlive || dead == healer || dead.Realm != healer.Realm ||
                    !dead.IsAutonomousWorldBot || dead.ObjectState != GameObject.eObjectState.Active)
                    continue;
                if (Claims.TryGetValue(dead, out var claim) && claim.Healer != healer && now < claim.Until && claim.Healer.IsAlive)
                    continue;
                if (fighting && EnemyNear(dead))
                    continue;
                int distance = healer.GetDistanceTo(dead);
                if (distance < bestDistance)
                {
                    best = dead;
                    bestDistance = distance;
                }
            }
            if (best != null)
            {
                if (Claims.Count > 4_096) Claims.Clear();
                Claims[best] = (healer, now + ClaimMilliseconds);
            }
            return best;
        }

        private static bool EnemyNear(GameBot corpse)
        {
            foreach (GameNPC npc in corpse.GetNPCsInRadius(EnemyClearRadius))
                // Enemy bots and their pets. Not keep guards: the dead lie at the gate under the walls, always within reach
                // of a guard, and players raised there all the same (run 23: 11 Midgard dead at Dun Dagda, none raised).
                if (npc.IsAlive && npc is not DOL.GS.Keeps.GameKeepGuard && npc.Realm != corpse.Realm &&
                    npc.Realm is eRealm.Albion or eRealm.Midgard or eRealm.Hibernia)
                    return true;
            foreach (GamePlayer player in corpse.GetPlayersInRadius(EnemyClearRadius))
                if (player.IsAlive && player.Realm != corpse.Realm)
                    return true;
            return false;
        }

        public static void Clear() => Claims.Clear();

        /// <summary>Why siege raises do or do not happen (run 23: 0 raises with dozens of attackers dead at Dun Dagda).</summary>
        public enum Check { UnderAttack, CannotCast, NoCorpse, Corpse, NotAttacker }
        private static readonly long[] Checks = new long[5];

        /// <summary>A fallen realm-mate lies within <see cref="RaiseRadius"/> (diagnostic only).</summary>
        public static bool HasFallenComradeNear(GameBot healer)
        {
            foreach (GameNPC npc in healer.GetNPCsInRadius(RaiseRadius))
                if (npc is GameBot dead && !dead.IsAlive && dead != healer && dead.Realm == healer.Realm && dead.IsAutonomousWorldBot)
                    return true;
            return false;
        }
        private static long _nextReport;

        public static void Note(Check check)
        {
            System.Threading.Interlocked.Increment(ref Checks[(int)check]);
            long now = GameLoop.GameLoopTime, next = System.Threading.Interlocked.Read(ref _nextReport);
            if (now < next || System.Threading.Interlocked.CompareExchange(ref _nextReport, now + 5 * 60_000, next) != next) return;
            long underAttack = System.Threading.Interlocked.Exchange(ref Checks[0], 0), cannotCast = System.Threading.Interlocked.Exchange(ref Checks[1], 0),
                 noCorpse = System.Threading.Interlocked.Exchange(ref Checks[2], 0), corpse = System.Threading.Interlocked.Exchange(ref Checks[3], 0),
                 notAttacker = System.Threading.Interlocked.Exchange(ref Checks[4], 0);
            if (next == 0) return;
            DOL.Logging.LoggerManager.Create(typeof(AutonomousSiegeResurrection)).Info(
                $"AUTONOMOUS_SIEGE_RAISE_CHECKS underAttack={underAttack} cannotCast={cannotCast} noCorpse={noCorpse} corpseClaimed={corpse} " +
                $"notAttackerWithDeadNear={notAttacker}");
        }
    }
}
