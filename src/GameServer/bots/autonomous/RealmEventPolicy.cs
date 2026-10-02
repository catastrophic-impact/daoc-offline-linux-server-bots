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

        public static bool SiegeReady(bool relic, bool deadline, int attackers, int defenders) =>
            attackers >= (deadline ? relic ? 48 : 32 : relic ? 170 : 108);

        public static bool AttackersReady(long started, long now, int assigned, int present) =>
            now >= started && now - started >= EarliestAssaultMilliseconds &&
            assigned > 0 && present >= assigned;

        public static bool CanReact(bool actualAttackObserved) => actualAttackObserved;
    }
}
