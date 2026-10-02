using System;

namespace DOL.GS
{
    /// <summary>Text only: never changes event clocks, membership or combat decisions.</summary>
    public static class RealmEventBanter
    {
        public static bool ReminderDue(bool started, bool sent, long remaining) =>
            !started && !sent && remaining > 0 && remaining <= 20 * 60_000L;

        public static string RaidReminder(string name, bool forced) => forced
            ? $"Sharpen your blades for {name}! About twenty minutes until our earliest assault; we still need our company assembled and the way clear."
            : $"The muster for {name} has about twenty minutes left. Gather your companions! We march once enough of us are ready, not before.";

        public static string RaidOutcome(string name, string outcome) => outcome switch
        {
            "Boss defeated" => Pick($"Victory at {name}! Raise a cup for those who stood beside us.",
                $"Word from {name}: the final foe has fallen! Honour to our realm and its brave company."),
            "Timed out" => Pick($"Our time at {name} is spent. We could not finish the fight; rally home and tend the wounded.",
                $"The expedition to {name} must withdraw. No victory this time, but we shall return."),
            "Failed rally" => $"Not enough of our company reached {name} in time. The muster is called off; save your strength for another day.",
            _ => $"The expedition at {name} has ended without a confirmed victory. Return safely, companions."
        };

        public static string SiegeReminder(string name, bool defender) => defender
            ? $"Stand ready at {name}! The enemy's muster has about twenty minutes left; keep shields and supplies close."
            : $"About twenty minutes remain to muster for {name}. Close ranks, warbands; we need a fighting force before committing to the walls.";

        public static string SiegeOutcome(string name, bool defender, bool timedOut, bool failedRally, eRealm winner, eRealm audience)
        {
            if (winner != eRealm.None)
                return winner == audience ? $"{name} is ours! Raise our banners and honour those who fought for it."
                    : $"{name} has fallen to {GlobalConstants.RealmToName(winner)}. Tend the wounded; this struggle is not over.";
            if (failedRally) return defender ? $"The enemy muster at {name} has broken up. The walls remain ours; stay watchful."
                : $"The muster for {name} failed to gather enough fighters in time. Stand down, warbands; there will be another campaign.";
            if (timedOut) return defender ? $"We held {name}! The enemy's time is spent and our banners still fly."
                : $"Our time to take {name} has run out. Withdraw in good order; we could not break the defence.";
            return $"The fighting for {name} has ended. Await confirmed word before claiming victory.";
        }

        private static string Pick(string first, string second) => Random.Shared.Next(2) == 0 ? first : second;
    }
}
