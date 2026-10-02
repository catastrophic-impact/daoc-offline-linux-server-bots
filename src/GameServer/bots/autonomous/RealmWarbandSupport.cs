using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DOL.GS
{
    /// <summary>Immutable, realm-separated support rosters for shared keep/relic events.</summary>
    public static class RealmWarbandSupport
    {
        public sealed record Roster(object Scope, GameLiving[] Members);
        private static Dictionary<Group, Roster> _groups = new();
        private static Dictionary<string, object> _scopes = new();
        private static long _next;
        public static Roster Get(GameBot bot) => bot?.Group != null &&
            Volatile.Read(ref _groups).TryGetValue(bot.Group, out var roster) &&
            AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR) ? roster : null;

        public static void Pulse(long now)
        {
            if (now < _next) return;
            _next = now + 10_000;
            var targets = AutonomousRvrEventLayer.ForceTargets();
            var grouped = new Dictionary<string, List<GameBot>>();
            foreach (GameBot bot in AutonomousBotRegistry.Snapshot())
            {
                if (bot.Group == null || !AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR)) continue;
                string force = bot.TempProperties.GetProperty<string>("RvrEventForce") ?? $"rvr-{bot.DatabaseID}";
                if (!targets.TryGetValue(force, out string target)) continue;
                string key = $"{target}/{bot.Realm}";
                if (!grouped.TryGetValue(key, out var members)) grouped[key] = members = new();
                members.Add(bot);
            }
            var groups = new Dictionary<Group, Roster>();
            var scopes = new Dictionary<string, object>();
            foreach (var pair in grouped)
            {
                object scope = _scopes.GetValueOrDefault(pair.Key) ?? new object();
                scopes[pair.Key] = scope;
                var roster = new Roster(scope, pair.Value.Cast<GameLiving>().ToArray());
                foreach (Group group in pair.Value.Select(b => b.Group).Where(g => g != null).Distinct()) groups[group] = roster;
            }
            _scopes = scopes;
            Volatile.Write(ref _groups, groups);
        }
    }
}
