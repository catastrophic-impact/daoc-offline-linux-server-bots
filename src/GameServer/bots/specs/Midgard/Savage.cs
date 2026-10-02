using System;
using System.Collections.Generic;
using DOL.Database;

namespace DOL.GS
{
    public class SavageBotSpec : BotSpec
    {
        public SavageBotSpec(eSpecType spec, eObjectType preferredWeapon = 0)
        {
            SpecName = "SavageBotSpec";

            if (preferredWeapon is eObjectType.Sword or eObjectType.Axe or eObjectType.Hammer or eObjectType.HandToHand)
                WeaponOneType = preferredWeapon;
            else
            {
                var randBaseWeap = spec switch
                {
                    eSpecType.TwoHanded => Util.Random(0, 2),
                    eSpecType.Mid => Util.Random(0, 2),
                    eSpecType.DualWield => Util.Random(3, 4),
                    _ => Util.Random(4),
                };

                WeaponOneType = randBaseWeap switch
                {
                    0 => eObjectType.Sword,
                    1 => eObjectType.Axe,
                    2 => eObjectType.Hammer,
                    _ => eObjectType.HandToHand,
                };
            }

            if (WeaponOneType != eObjectType.HandToHand)
            {
                Is2H = true;
                SpecType = eSpecType.Mid;
            }
            else
                SpecType = eSpecType.DualWield;

            int randVariance = Util.Random(3);

            switch (randVariance)
            {
                case 0:
                Add(ObjToSpec(WeaponOneType), 44, 1.0f);
                Add(Specs.Savagery, 49, 0.75f);
                Add(Specs.Parry, 4, 0.0f);
                break;

                case 1:
                Add(ObjToSpec(WeaponOneType), 39, 1.0f);
                Add(Specs.Savagery, 49, 0.75f);
                Add(Specs.Parry, 20, 0.1f);
                break;

                case 2:
                Add(ObjToSpec(WeaponOneType), 44, 1.0f);
                Add(Specs.Savagery, 48, 0.75f);
                Add(Specs.Parry, 10, 0.1f);
                break;

                case 3:
                Add(ObjToSpec(WeaponOneType), 50, 1.0f);
                Add(Specs.Savagery, 42, 0.75f);
                Add(Specs.Parry, 9, 0.1f);
                break;
            }
        }

        /// <summary>
        /// Keeps a persistent Savage on the weapon line it already trained.
        /// The generic TwoHanded profile covers three independent Midgard lines;
        /// rerolling that choice on every restart could equip Axe while all saved
        /// points and styles remained Sword (or vice versa).
        /// </summary>
        public static eObjectType WeaponFromPersistedSpecs(string serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized))
                return 0;

            Dictionary<string, eObjectType> weapons = new(StringComparer.OrdinalIgnoreCase)
            {
                [Specs.Sword] = eObjectType.Sword,
                [Specs.Axe] = eObjectType.Axe,
                [Specs.Hammer] = eObjectType.Hammer,
                [Specs.HandToHand] = eObjectType.HandToHand,
            };
            // Every weapon line can exist at its baseline rank. Only a unique
            // investment above rank one proves which line the bot chose.
            int bestLevel = 1;
            eObjectType best = 0;
            bool tied = false;
            foreach (string entry in serialized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = entry.Split('|', 2);
                if (parts.Length != 2 || !weapons.TryGetValue(parts[0], out eObjectType weapon) ||
                    !int.TryParse(parts[1], out int level))
                    continue;
                if (level > bestLevel)
                {
                    bestLevel = level;
                    best = weapon;
                    tied = false;
                }
                else if (level == bestLevel && weapon != best)
                    tied = true;
            }
            return tied ? 0 : best;
        }

        public static bool AlignWithPersistedSpecs(BotSpec plan, string serialized)
        {
            eObjectType trained = WeaponFromPersistedSpecs(serialized);
            if (plan == null || trained == 0 || plan.WeaponOneType == trained &&
                plan.SpecType == (trained == eObjectType.HandToHand ? eSpecType.DualWield : eSpecType.Mid) &&
                plan.Is2H == (trained != eObjectType.HandToHand))
                return false;

            string previousLine = SkillBase.ObjectTypeToSpec(plan.WeaponOneType);
            string trainedLine = SkillBase.ObjectTypeToSpec(trained);
            for (int i = 0; i < plan.SpecLines.Count; i++)
            {
                BotSpecLine line = plan.SpecLines[i];
                if (line.Spec == previousLine)
                    plan.SpecLines[i] = new BotSpecLine(trainedLine, line.SpecCap, line.levelRatio);
            }
            plan.WeaponOneType = trained;
            plan.WeaponTwoType = 0;
            plan.SpecType = trained == eObjectType.HandToHand ? eSpecType.DualWield : eSpecType.Mid;
            plan.Is2H = trained != eObjectType.HandToHand;
            return true;
        }
    }

    /// <summary>
    /// Compares only the Savage's trained weapon line. The server caps weapon
    /// DPS by character level and applies quality, condition, and a two-hand
    /// bonus to actual melee damage, so item level alone is not a useful rank.
    /// </summary>
    public static class SavageBotWeaponPolicy
    {
        public static double CombatScore(GameBot bot, DbInventoryItem item, eInventorySlot slot)
        {
            return CombatScore(bot, item, slot, true);
        }

        private static double CombatScore(GameBot bot, DbInventoryItem item, eInventorySlot slot,
            bool checkProficiency)
        {
            if (bot?.CharacterClass?.ID != (int)eCharacterClass.Savage || bot.BotSpec == null ||
                item == null || (eObjectType)item.Object_Type != bot.BotSpec.WeaponOneType ||
                slot is not (eInventorySlot.RightHandWeapon or eInventorySlot.LeftHandWeapon or eInventorySlot.TwoHandWeapon) ||
                slot != eInventorySlot.TwoHandWeapon && item.Item_Type is not (Slot.RIGHTHAND or Slot.LEFTHAND) ||
                slot != eInventorySlot.TwoHandWeapon && item.Hand == 1 ||
                // Hand=2 marks a weapon as left-hand capable. Every generated
                // hand-to-hand claw carries it ("all hand to hand weapons usable
                // in left hand") and the native inventory accepts such items in
                // the main hand, so claws are valid in either hand. Other Hand=2
                // weapon types keep the old main-hand rejection.
                slot == eInventorySlot.RightHandWeapon && item.Hand == 2 &&
                    (eObjectType)item.Object_Type != eObjectType.HandToHand ||
                slot == eInventorySlot.TwoHandWeapon && item.Item_Type != Slot.TWOHAND ||
                slot == eInventorySlot.LeftHandWeapon && bot.BotSpec.WeaponOneType != eObjectType.HandToHand ||
                slot == eInventorySlot.TwoHandWeapon && bot.BotSpec.WeaponOneType == eObjectType.HandToHand ||
                !BotWeaponStats.HasFunctionalMeleeStats(item) || item.LevelRequirement > bot.Level ||
                checkProficiency && !BotWeaponStats.FitsConfiguredSlot(bot, item, slot))
                return 0;

            int cap = BotWeaponStats.NormalDps(bot.Level) + (bot.RealmLevel > 39 ? 3 : 0);
            double score = Math.Min(item.DPS_AF, cap) * item.Quality * item.ConditionPercent;
            if (slot == eInventorySlot.TwoHandWeapon)
                score *= 1.1 + (checkProficiency
                    ? Math.Max(0, bot.WeaponSpecLevel((eObjectType)item.Object_Type, Slot.TWOHAND)) * 0.005
                    : 0);
            return score;
        }

        public static bool TryBestActiveSlot(GameBot bot, out eActiveWeaponSlot slot)
        {
            return TryBestActiveSlot(bot, out slot, true);
        }

        // SetWeapons creates only the Savage plan's own starter items. During
        // companion construction the server rules service may not yet exist;
        // the generated class/shape checks above are enough for this one call.
        public static bool TryBestGeneratedStarterSlot(GameBot bot, out eActiveWeaponSlot slot)
        {
            return TryBestActiveSlot(bot, out slot, false);
        }

        private static bool TryBestActiveSlot(GameBot bot, out eActiveWeaponSlot slot,
            bool checkProficiency)
        {
            slot = eActiveWeaponSlot.Standard;
            if (bot?.Inventory == null || bot.CharacterClass?.ID != (int)eCharacterClass.Savage)
                return false;

            double oneHand = CombatScore(bot, bot.Inventory.GetItem(eInventorySlot.RightHandWeapon),
                eInventorySlot.RightHandWeapon, checkProficiency);
            double twoHand = CombatScore(bot, bot.Inventory.GetItem(eInventorySlot.TwoHandWeapon),
                eInventorySlot.TwoHandWeapon, checkProficiency);
            if (oneHand <= 0 && twoHand <= 0)
                return false;

            // Keep the current choice on an exact tie to avoid restarting a
            // swing when both weapon shapes deal the same effective damage.
            if (oneHand == twoHand)
                slot = bot.ActiveWeaponSlot == eActiveWeaponSlot.TwoHanded
                    ? eActiveWeaponSlot.TwoHanded : eActiveWeaponSlot.Standard;
            else if (twoHand > oneHand)
                slot = eActiveWeaponSlot.TwoHanded;
            return true;
        }

        public static double UpgradeGain(GameBot bot, DbInventoryItem candidate, eInventorySlot target)
        {
            double candidateScore = CombatScore(bot, candidate, target);
            if (candidateScore <= 0 || bot?.Inventory == null)
                return 0;

            double currentScore = CombatScore(bot, bot.Inventory.GetItem(target), target);
            if (target != eInventorySlot.LeftHandWeapon)
            {
                eInventorySlot other = target == eInventorySlot.TwoHandWeapon
                    ? eInventorySlot.RightHandWeapon : eInventorySlot.TwoHandWeapon;
                currentScore = Math.Max(currentScore,
                    CombatScore(bot, bot.Inventory.GetItem(other), other));
            }
            return candidateScore - currentScore;
        }

        public static bool IsUpgrade(GameBot bot, DbInventoryItem candidate, eInventorySlot target) =>
            UpgradeGain(bot, candidate, target) > 0;
    }
}
