using System;
using System.Collections.Generic;
using System.Linq;

namespace DOL.GS
{
    /// <summary>Bounded per-keep jobs. Weak references and expiring leases cannot retain removed bots.</summary>
    public static class AutonomousSiegeJobs
    {
        private sealed record Lease(WeakReference<GameBot> Bot, long Until, BotSiegeKind Kind);
        private static readonly object Gate = new();
        private static readonly Dictionary<(ushort Region, string Keep, eRealm Realm, int Slot), Lease> Jobs = new();
        // Run 2026-10-07 (Midgard on Caer Erasleigh): 17 attackers got two jobs, a carried catapult took one, and a
        // single ram wore the gate down 3% a minute. A small force still brings one ram; an army brings both rams and
        // the trebuchet.
        public static int Slots(int present) => present < 4 ? 0 : present < 8 ? 1 : present < 24 ? 3 : present < 64 ? 5 : 6;
        public static bool CanOperate(eCharacterClass characterClass) => characterClass is
            eCharacterClass.Armsman or eCharacterClass.Mercenary or eCharacterClass.Paladin or eCharacterClass.Reaver or eCharacterClass.Scout or
            eCharacterClass.Warrior or eCharacterClass.Berserker or eCharacterClass.Savage or eCharacterClass.Thane or
            eCharacterClass.Hero or eCharacterClass.Champion or eCharacterClass.Blademaster or eCharacterClass.Ranger or eCharacterClass.Valewalker;
        public static bool Eligible(GameBot bot) => bot is { IsAutonomousWorldBot: true, IsPlayerLedGroup: false, IsAlive: true } &&
            bot.CharacterClass != null && CanOperate((eCharacterClass)bot.CharacterClass.ID);

        /// <summary>Attackers: two rams, the trebuchet (triple damage against doors), a catapult, ballistas.
        /// Defenders: ballistas against siege, catapults on the attackers; never a trebuchet, whose 2,000 minimum
        /// range has no spot inside the keep (it logged placement_blocked with no candidate at all).</summary>
        public static BotSiegeKind SlotKind(int index, bool attacking) => attacking
            ? index < 2 ? BotSiegeKind.Ram : index == 2 ? BotSiegeKind.Trebuchet : index == 3 ? BotSiegeKind.Catapult : BotSiegeKind.Ballista
            : index < 2 ? BotSiegeKind.Ballista : index < 4 ? BotSiegeKind.Catapult : BotSiegeKind.Ballista;

        /// <summary>Slot search order: slots matching a kit the bot already carries come first.</summary>
        public static int[] SlotOrder(int count, bool attacking, BotSiegeKind? carried) =>
            Enumerable.Range(0, count).OrderBy(index => carried.HasValue && SlotKind(index, attacking) == carried.Value ? 0 : 1)
                .ThenBy(index => index).ToArray();

        public static bool TryAcquire(GameBot bot, string keep, int present, bool attacking, bool enemyEngines,
            out BotSiegeKind kind, out int slot, ushort? destinationRegion = null, BotSiegeKind? carried = null)
        {
            kind = default; slot = -1;
            if (!Eligible(bot)) return false;
            ushort region = destinationRegion ?? bot.CurrentRegionID;
            lock (Gate)
            {
                long now = GameLoop.GameLoopTime;
                foreach (var key in Jobs.Where(p => p.Value.Until < now || !p.Value.Bot.TryGetTarget(out GameBot b) || !b.IsAlive ||
                    b.ObjectState != GameObject.eObjectState.Active).Select(p => p.Key).ToArray()) Jobs.Remove(key);
                var existing = Jobs.FirstOrDefault(p => p.Key.Keep == keep && p.Key.Realm == bot.Realm &&
                    p.Key.Region == region && p.Value.Bot.TryGetTarget(out GameBot b) && b == bot);
                if (existing.Value != null)
                {
                    Jobs[existing.Key] = existing.Value with { Until = now + 120_000 };
                    bot.TempProperties.SetProperty("SiegeJobUntil", now + 120_000);
                    kind = existing.Value.Kind; slot = existing.Key.Slot; return true;
                }
                foreach (int index in SlotOrder(Math.Min(Slots(present), attacking ? 6 : 4), attacking, carried))
                {
                    var key = (region, keep, bot.Realm, index);
                    if (Jobs.ContainsKey(key)) continue;
                    kind = SlotKind(index, attacking);
                    if (kind == BotSiegeKind.Ballista && !enemyEngines) continue;
                    Jobs[key] = new(new(bot), now + 120_000, kind);
                    bot.TempProperties.SetProperty("SiegeJobUntil", now + 120_000);
                    slot = index; return true;
                }
                return false;
            }
        }
        public static void Release(GameBot bot)
        {
            bot?.TempProperties.SetProperty("SiegeJobUntil", 0L);
            lock (Gate) foreach (var key in Jobs.Where(p => p.Value.Bot.TryGetTarget(out GameBot b) && b == bot).Select(p => p.Key).ToArray()) Jobs.Remove(key);
        }
        public static void Refresh(GameBot bot)
        {
            lock (Gate) foreach (var key in Jobs.Where(p => p.Value.Bot.TryGetTarget(out GameBot b) && b == bot).Select(p=>p.Key).ToArray())
                Jobs[key] = Jobs[key] with { Until = GameLoop.GameLoopTime + 120_000 };
            bot.TempProperties.SetProperty("SiegeJobUntil", GameLoop.GameLoopTime + 120_000);
        }
    }
}
