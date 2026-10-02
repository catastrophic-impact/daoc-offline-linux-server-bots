using DOL.Database;
using DOL.GS.Styles;

namespace DOL.GS
{
    /// <summary>Shared companion/world-bot selection; native openings and costs remain authoritative.</summary>
    public static class BotMeleeStylePolicy
    {
        public static bool IsEligible(Style style) => style != null &&
            style.OpeningRequirementType != Style.eOpening.Positional && !style.StealthRequirement;

        public static bool Better(Style candidate, Style current) => current == null ||
            candidate.GrowthRate > current.GrowthRate ||
            candidate.GrowthRate == current.GrowthRate && candidate.Level > current.Level;

        public static DbInventoryItem Weapon(GameBot bot, Style style) =>
            style?.WeaponTypeRequirement == (int)eObjectType.Shield && bot.ActiveWeaponSlot == eActiveWeaponSlot.Standard
                ? bot.Inventory.GetItem(eInventorySlot.LeftHandWeapon) : bot.ActiveWeapon;

        public static bool Usable(GameBot bot, Style style, AttackData lastAttack) =>
            IsEligible(style) && style.Level <= bot.Level &&
            MatchesWeapon(style, bot.ActiveWeapon, bot.Inventory.GetItem(eInventorySlot.LeftHandWeapon), bot.ActiveWeaponSlot) &&
            StyleProcessor.CheckEnduranceCost(bot, Weapon(bot, style), style) &&
            bot.CheckStyleStun(style) &&
            StyleProcessor.CanUseStyle(lastAttack, bot, style, Weapon(bot, style));

        public static bool MatchesWeapon(Style style, DbInventoryItem primary, DbInventoryItem left, eActiveWeaponSlot slot)
        {
            if (style == null || primary == null || slot == eActiveWeaponSlot.Distance) return false;
            if (style.WeaponTypeRequirement == Style.SpecialWeaponType.DualWield)
            {
                if (slot != eActiveWeaponSlot.Standard || left == null ||
                    primary.Item_Type is not Slot.RIGHTHAND and not Slot.LEFTHAND ||
                    left.Object_Type == (int)eObjectType.Shield) return false;
                if (style.Spec == Specs.HandToHand)
                    return primary.Object_Type == (int)eObjectType.HandToHand && left.Object_Type == (int)eObjectType.HandToHand;
                if (style.Spec == Specs.Left_Axe) return left.Object_Type is (int)eObjectType.LeftAxe or (int)eObjectType.Axe;
                return GlobalConstants.IsWeapon(left.Object_Type);
            }
            if (style.WeaponTypeRequirement == Style.SpecialWeaponType.AnyWeapon)
                return GlobalConstants.IsWeapon(primary.Object_Type) && primary.Object_Type != (int)eObjectType.Shield;
            if (style.WeaponTypeRequirement == (int)eObjectType.Shield)
                return slot == eActiveWeaponSlot.Standard && left?.Object_Type == (int)eObjectType.Shield;
            eObjectType type = primary.Object_Type == (int)eObjectType.LeftAxe ? eObjectType.Axe : (eObjectType)primary.Object_Type;
            return GameServer.ServerRules.IsObjectTypesEqual((eObjectType)style.WeaponTypeRequirement, type);
        }

        public static Style Select(GameBot bot, AttackData lastAttack, bool preserveQueued = true)
        {
            if (bot?.Styles == null || !BotWeaponStats.FitsConfiguredSlot(bot, bot.ActiveWeapon,
                    bot.ActiveWeaponSlot == eActiveWeaponSlot.TwoHanded ? eInventorySlot.TwoHandWeapon : eInventorySlot.RightHandWeapon) ||
                bot.ActiveWeaponSlot == eActiveWeaponSlot.Distance)
                return null;
            // Preserve a deliberate tank taunt, but revalidate at the actual swing.
            Style queued = bot.styleComponent.NextCombatStyle;
            if (preserveQueued && Usable(bot, queued, lastAttack)) return queued;
            Style best = null;
            Style anytime = null;
            foreach (Style style in bot.Styles)
            {
                if (!Usable(bot, style, lastAttack)) continue;
                if (Better(style, best)) best = style;
                if (SavageBotCombatPolicy.IsReliableAnytimeStyle(style) && Better(style, anytime)) anytime = style;
            }
            bot.styleComponent.NextCombatBackupStyle = anytime;
            return best;
        }
    }
}
