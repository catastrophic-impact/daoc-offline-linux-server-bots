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
        public static int Slots(int present) => present < 8 ? 0 : present < 24 ? 2 : present < 64 ? 4 : 6;
        public static bool CanOperate(eCharacterClass characterClass) => characterClass is
            eCharacterClass.Armsman or eCharacterClass.Mercenary or eCharacterClass.Paladin or eCharacterClass.Reaver or eCharacterClass.Scout or
            eCharacterClass.Warrior or eCharacterClass.Berserker or eCharacterClass.Savage or eCharacterClass.Thane or
            eCharacterClass.Hero or eCharacterClass.Champion or eCharacterClass.Blademaster or eCharacterClass.Ranger or eCharacterClass.Valewalker;
        public static bool Eligible(GameBot bot) => bot is { IsAutonomousWorldBot: true, IsPlayerLedGroup: false, IsAlive: true } &&
            bot.CharacterClass != null && CanOperate((eCharacterClass)bot.CharacterClass.ID);

        public static bool TryAcquire(GameBot bot, string keep, int present, bool attacking, bool enemyEngines,
            out BotSiegeKind kind, out int slot, ushort? destinationRegion = null)
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
                for (int index = 0; index < Math.Min(Slots(present), attacking ? 6 : 4); index++)
                {
                    var key = (region, keep, bot.Realm, index);
                    if (Jobs.ContainsKey(key)) continue;
                    kind = index < 2 ? (attacking ? BotSiegeKind.Ram : BotSiegeKind.Ballista) :
                        index < 4 ? (index == 2 ? BotSiegeKind.Catapult : BotSiegeKind.Trebuchet) : BotSiegeKind.Ballista;
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
