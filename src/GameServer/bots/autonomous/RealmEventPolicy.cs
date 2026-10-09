using System;

namespace DOL.GS
{
    /// <summary>Population-independent event timing. Actual world combat decides outcomes.</summary>
    public static class RealmEventPolicy
    {
        public const int DragonAttackDamageLimit = 600;

        // Include criticals and resistance debuffs; never raise weaker hits.
        public static void CapDragonDamage(AttackData attack)
        {
            if (attack == null) return;
            attack.Damage = Math.Clamp(attack.Damage, 0, DragonAttackDamageLimit);
            attack.CriticalDamage = Math.Clamp(attack.CriticalDamage, 0, DragonAttackDamageLimit - attack.Damage);
        }
        public const long EarliestAssaultMilliseconds = 3 * 60_000L;

        public static long RecruitmentMilliseconds(double roll) =>
            60 * 60_000L;

        public static bool CanRecruitRealm(bool defender, bool attackObserved, int attackers, int defenders, int cap) =>
            attackObserved || (defender ? attackers >= Math.Max(8, cap / 8) :
                attackers >= cap / 4 && defenders >= cap / 4);

        // Dynamic sieges (owner, 2026-10-07): defenders rally once the attack actually lands; a third realm
        // intervenes only sometimes, a few minutes into the battle.
        public const double ThirdRealmChance = 0.5;
        public const long ThirdRealmDelayMilliseconds = 5 * 60_000L;

        public static bool CanRespond(bool defender, bool attackObserved, bool battleStarted, bool thirdRealmIntervenes,
            long sinceBattleMilliseconds) =>
            defender ? attackObserved || battleStarted
                : thirdRealmIntervenes && battleStarted && sinceBattleMilliseconds >= ThirdRealmDelayMilliseconds;

        /// <summary>
        /// Dynamic siege sizes (owner, 2026-10-07 evening): a keep siege is up to 128 attackers against 64
        /// defenders, a relic siege 240 against 120. Attackers start with the edge (walls, doors and keep guards
        /// are the defenders' extra); the defenders gather as one quick rally and go in together instead of
        /// trickling. The third realm interferes half the time, with at most half the attackers (64 / 120).
        /// </summary>
        public const int KeepAttackers = 128;
        public const int RelicAttackers = 240;

        [DOL.GS.ServerProperties.ServerProperty("autonomous", "rvr_siege_defense_ratio",
            "Bot defenders a siege may draw, as a share of its attacker cap (128 keep / 240 relic); walls, doors and keep guards are extra.", 0.5)]
        public static double DefenseRatio = 0.5;

        public static int AttackerCap(bool relic) => relic ? RelicAttackers : KeepAttackers;
        public static int DefenderCap(bool relic) => Math.Max(8, (int)Math.Round(AttackerCap(relic) * Math.Clamp(DefenseRatio, 0.25, 2.0)));
        public static int ThirdRealmCap(bool relic) => AttackerCap(relic) / 2;

        /// <summary>Attackers needed to start the battle: 85% of the cap at the posts, half of it on a strike or at the deadline.</summary>
        public static int DynamicStartThreshold(bool relic, bool deadline) =>
            deadline ? AttackerCap(relic) / 2 : AttackerCap(relic) * 85 / 100;

        public static bool SiegeReady(bool relic, bool deadline, int attackers, int defenders) =>
            attackers >= (deadline ? relic ? 48 : 32 : relic ? 170 : 108);

        public static bool AttackersReady(long started, long now, int assigned, int present) =>
            now >= started && now - started >= EarliestAssaultMilliseconds &&
            assigned > 0 && present >= assigned;

        public static bool CanReact(bool actualAttackObserved) => actualAttackObserved;
    }
}
