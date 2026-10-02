using System;
using System.Collections.Generic;
using DOL.Database;

namespace DOL.GS
{
    public sealed class BountyRewardResult
    {
        public bool Granted { get; init; }
        public string Reason { get; init; }
        public long ExperienceGranted { get; init; }
        public int GoldGranted { get; init; }
        public long MoneyGranted { get; init; }
        public int ItemCount { get; init; }
    }

    public static class BountyRewardService
    {
        private const int EpicItemLevelForUtility = 100;
        private const int EpicMinimumUtility = 72;
        private const int EpicItemAttempts = 50;

        /// <summary>
        /// Pays a completed bounty. The quest must call this once while holding its
        /// own completion lock, and finish only when Granted is true.
        /// </summary>
        public static BountyRewardResult Grant(GamePlayer player, byte assignedLevel, bool rerolled)
        {
            if (player == null || assignedLevel is < 1 or > 50)
                return new() { Reason = "Invalid bounty reward." };

            int itemCount = Util.Random(1, 3);
            var items = new List<DbInventoryItem>(itemCount);
            var realm = player.Realm;
            var playerClass = (eCharacterClass)player.CharacterClass.ID;

            for (int i = 0; i < itemCount; i++)
            {
                GeneratedUniqueItem template = assignedLevel == 50
                    ? GenerateEpicItem(realm, playerClass)
                    : GenerateLevelingItem(realm, playerClass, assignedLevel);

                if (template == null)
                    return new() { Reason = "No suitable equipment reward could be generated. Please try again." };

                DbInventoryItem item = GameInventoryItem.Create<DbItemUnique>(template);
                item.IsROG = true;
                items.Add(item);
            }

            long gold = assignedLevel == 50 ? Money.GetMoney(0, 0, 100, 0, 0) : 0;
            long xp = assignedLevel == 50 ? 0 : CalculateExperienceReward(
                assignedLevel, rerolled, ServerProperties.Properties.XP_RATE);

            // Reserve actual backpack slots before paying XP or gold. AddItem can
            // still reject an item, in which case restore the inventory and leave
            // the bounty unfinished for a safe retry.
            lock (player.Inventory.Lock)
            {
                var freeSlots = new List<eInventorySlot>(itemCount);
                for (eInventorySlot slot = eInventorySlot.FirstBackpack;
                     slot <= eInventorySlot.LastBackpack && freeSlots.Count < itemCount; slot++)
                {
                    if (player.Inventory.GetItem(slot) == null)
                        freeSlots.Add(slot);
                }

                if (freeSlots.Count < itemCount)
                    return new() { Reason = $"Make room for {itemCount} bounty item(s), then speak to the Bounty Master again." };

                var added = new List<DbInventoryItem>(itemCount);
                for (int i = 0; i < itemCount; i++)
                {
                    if (!player.Inventory.AddItem(freeSlots[i], items[i]))
                    {
                        foreach (DbInventoryItem item in added)
                            player.Inventory.RemoveItem(item);
                        return new() { Reason = "Your equipment could not be placed in your backpack. Please try again." };
                    }
                    added.Add(items[i]);
                }
            }

            if (gold > 0)
                player.AddMoney(gold, "You receive {0} as a bounty reward.");

            long experienceBefore = player.Experience;
            if (xp > 0 && player.Level < GamePlayer.MAX_LEVEL)
                ApplyExperience(player, xp);

            return new()
            {
                Granted = true,
                ExperienceGranted = player.Experience - experienceBefore,
                GoldGranted = assignedLevel == 50 ? 100 : 0,
                MoneyGranted = gold,
                ItemCount = itemCount
            };
        }

        /// <summary>
        /// One bulb is one tenth of the assigned level's XP width. The multiplier
        /// is applied here exactly once; ForceGainExperience does not apply it.
        /// </summary>
        public static long CalculateExperienceReward(byte assignedLevel, bool rerolled, double xpRate)
        {
            if (assignedLevel is < 1 or >= 50 || double.IsNaN(xpRate) || xpRate <= 0)
                return 0;

            long current = GamePlayer.GetExperienceAmountForLevel(assignedLevel - 1);
            long next = GamePlayer.GetExperienceAmountForLevel(assignedLevel);
            decimal rate = (decimal)Math.Min(xpRate, 1000d);
            decimal amount = (next - current) * (rerolled ? 0.1m : 0.2m) * rate;
            return (long)Math.Min(long.MaxValue, decimal.Round(amount, 0, MidpointRounding.AwayFromZero));
        }

        private static GeneratedUniqueItem GenerateLevelingItem(eRealm realm, eCharacterClass playerClass, byte currentLevel)
        {
            byte itemLevel = (byte)Math.Min(51, currentLevel + 1);
            var item = AtlasROGManager.GenerateMonsterLootROG(realm, playerClass, itemLevel, false);
            item.AllowAdd = true;
            return item;
        }

        private static GeneratedUniqueItem GenerateEpicItem(eRealm realm, eCharacterClass playerClass)
        {
            for (int attempt = 0; attempt < EpicItemAttempts; attempt++)
            {
                // The generator caps equipment level at 51 after it computes
                // utility. A higher generation level gives a genuine epic utility
                // budget while preserving wearable level-51 equipment.
                var item = new GeneratedUniqueItem(realm, playerClass,
                    EpicItemLevelForUtility, EpicMinimumUtility);
                if (item.BountyUtility < EpicMinimumUtility)
                    continue;

                item.Level = 51;
                item.Quality = 100;
                item.AllowAdd = true;
                item.IsTradable = true;
                item.Description = "Epic Bounty Reward | " + playerClass;
                return item;
            }
            return null;
        }

        private static void ApplyExperience(GamePlayer player, long amount)
        {
            long cap = GamePlayer.GetExperienceAmountForLevel(GamePlayer.MAX_LEVEL - 1);
            long target = Math.Min(cap, player.Experience + Math.Min(amount, cap));

            while (player.Experience < target && player.Level < GamePlayer.MAX_LEVEL)
            {
                long boundary = player.ExperienceForNextLevel;
                if (player.Level >= 40 && !player.IsLevelSecondStage)
                    boundary = Math.Min(boundary, player.ExperienceForCurrentLevelSecondStage);

                long next = Math.Min(target, boundary);
                if (next <= player.Experience)
                {
                    // The base-class trainer gate can leave raw XP banked above
                    // the displayed level. Do not discard the earned bounty XP.
                    player.ForceGainExperience(target - player.Experience);
                    break;
                }

                long previous = player.Experience;
                player.ForceGainExperience(next - previous);
                if (player.Experience <= previous)
                    break;
            }
        }
    }
}
