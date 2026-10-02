using System;
using DOL.Database;

namespace DOL.GS
{
    /// <summary>Equipment eligibility only; never changes the combat damage formula.</summary>
    public static class BotWeaponStats
    {
        // The normal weapon DPS reference, stored in tenths, expressed exactly
        // in integers. This is an equipment invariant, not a damage multiplier.
        public static int NormalDps(int itemLevel) => 12 + 3 * Math.Clamp(itemLevel, 0, 51);

        public static bool IsMeleeWeapon(eObjectType type) => type is
            eObjectType.CrushingWeapon or eObjectType.SlashingWeapon or eObjectType.ThrustWeapon or
            eObjectType.TwoHandedWeapon or eObjectType.PolearmWeapon or eObjectType.Staff or
            eObjectType.Sword or eObjectType.Hammer or eObjectType.Axe or eObjectType.LeftAxe or
            eObjectType.Spear or eObjectType.HandToHand or eObjectType.Blades or eObjectType.Blunt or
            eObjectType.Piercing or eObjectType.LargeWeapons or eObjectType.CelticSpear or
            eObjectType.Flexible or eObjectType.Scythe or eObjectType.MaulerStaff or eObjectType.FistWraps;

        private static bool HasDamageFields(DbItemTemplate item, int minimumDps) =>
            item.DPS_AF >= minimumDps && item.SPD_ABS > 0 &&
            item.Quality > 0 && item.MaxCondition > 0;

        // Earned loot retains its authored level/stats. Reject only unusable
        // melee data, including sub-1-DPS outliers; normal training weapons
        // are level zero / 1.2 DPS and must remain valid.
        public static bool HasFunctionalMeleeStats(DbItemTemplate item) => item != null &&
            (!IsMeleeWeapon((eObjectType)item.Object_Type) || HasDamageFields(item, 10));

        public static bool HasFunctionalMeleeStats(DbInventoryItem item) => item?.Template != null &&
            HasFunctionalMeleeStats(item.Template) &&
            (!IsMeleeWeapon((eObjectType)item.Object_Type) || item.Condition > 0);

        /// <summary>
        /// Runtime melee-slot validation shared by autonomous and temporary
        /// bots.  Merely occupying a weapon slot is not enough: old generated
        /// loadouts can contain a focus staff or another class-incompatible
        /// item, which made a Savage select it instead of closing with a legal
        /// axe or hand-to-hand weapon.
        /// </summary>
        public static bool IsConfiguredMeleeWeapon(GameBot bot, DbItemTemplate item)
        {
            if (bot == null || item == null || !IsMeleeWeapon((eObjectType)item.Object_Type))
                return false;

            BotSpec spec = bot.BotSpec;
            if (spec == null || spec.WeaponOneType == 0 && spec.WeaponTwoType == 0)
                return HasConfiguredWeaponProficiency(bot, item);

            eObjectType type = (eObjectType)item.Object_Type;
            return MatchesBuild(spec, type);
        }

        public static bool MatchesBuild(BotSpec spec, eObjectType type)
        {
            if (type == spec.WeaponOneType && type != 0) return true;
            if (type != spec.WeaponTwoType || type == 0) return false;
            if ((spec.SpecType is eSpecType.SluaghbinderBane or eSpecType.SluaghbinderCovenant) &&
                type == eObjectType.Scythe)
                return true;
            if (spec.SpecType == eSpecType.LeftAxe && type is eObjectType.Axe or eObjectType.LeftAxe) return true;
            string line = SkillBase.ObjectTypeToSpec(type);
            foreach (BotSpecLine planned in spec.SpecLines)
                if (planned.Spec == line && planned.SpecCap > 1) return true;
            return false;
        }

        public static bool CanUseMelee(GameBot bot, DbInventoryItem item) =>
            bot != null && item?.Template != null &&
            IsConfiguredMeleeWeapon(bot, item.Template) &&
            HasFunctionalMeleeStats(item) &&
            item.LevelRequirement <= bot.Level &&
            HasConfiguredWeaponProficiency(bot, item.Template);

        /// <summary>
        /// Reavers can have a real Flexible specialization while their
        /// generated ability list is still catching up after a persisted
        /// build is loaded.  The server's normal item check keys off the
        /// ability object, so that short load window made a legal one-handed
        /// flexible weapon look unusable and caused repeated starter-weapon
        /// warnings.  Keep the normal ability gate for every other class and
        /// accept Flexible only when the Reaver has actually trained the line.
        /// </summary>
        public static bool HasConfiguredWeaponProficiency(GameBot bot, DbItemTemplate item)
        {
            if (bot == null || item == null)
                return false;
            if (bot.HasAbilityToUseItem(item))
                return true;
            return bot.CharacterClass?.ID == (int)eCharacterClass.Reaver &&
                   (eObjectType)item.Object_Type == eObjectType.Flexible &&
                   bot.GetModifiedSpecLevel(Specs.Flexible) > 0;
        }

        public static eObjectType PrimaryType(eObjectType first, eObjectType second, bool twoHanded) =>
            twoHanded && second is eObjectType.TwoHandedWeapon or eObjectType.PolearmWeapon or
                eObjectType.LargeWeapons or eObjectType.CelticSpear or eObjectType.Scythe ? second :
                first != 0 ? first : second;

        public static bool FitsConfiguredSlot(GameBot bot, DbInventoryItem item, eInventorySlot slot)
        {
            if (!CanUseMelee(bot, item)) return false;
            if (slot is eInventorySlot.RightHandWeapon or eInventorySlot.LeftHandWeapon &&
                (item.Item_Type == Slot.TWOHAND || item.Hand == 1)) return false;
            if (slot == eInventorySlot.TwoHandWeapon && item.Item_Type != Slot.TWOHAND) return false;
            if (slot == eInventorySlot.LeftHandWeapon && bot.BotSpec?.SpecType == eSpecType.LeftAxe)
                return item.Object_Type is (int)eObjectType.Axe or (int)eObjectType.LeftAxe;
            if (slot is eInventorySlot.RightHandWeapon or eInventorySlot.TwoHandWeapon &&
                bot.BotSpec?.SpecType == eSpecType.LeftAxe)
                return item.Object_Type == (int)bot.BotSpec.WeaponOneType;
            return true;
        }

        /// <summary>
        /// Identifies only the old GameBot-generated inventory rows whose
        /// unique template was never persisted.  Their relation loads as the
        /// blank DbItemTemplate placeholder and can occupy a weapon slot while
        /// being impossible to equip.  Earned loot and valid unique items are
        /// deliberately excluded.
        /// </summary>
        public static bool IsBrokenGeneratedFallback(DbInventoryItem item) =>
            item != null &&
            string.Equals(item.Creator, nameof(GameBot), StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(item.UTemplate_Id) &&
            string.IsNullOrWhiteSpace(item.ITemplate_Id) &&
            item.Template is not DbItemUnique;

        public static bool NeedsCompanionDamageStats(eObjectType type) =>
            IsMeleeWeapon(type) || BotRangedCombat.IsRangedWeaponType(type);

        public static bool IsNormalCompanionWeapon(DbItemTemplate item) => item != null &&
            (!NeedsCompanionDamageStats((eObjectType)item.Object_Type) ||
                (HasDamageFields(item, NormalDps(item.Level)) && item.SPD_ABS >= 20 &&
                    item.Quality >= 85 && item.MaxDurability > 0));
    }
}
