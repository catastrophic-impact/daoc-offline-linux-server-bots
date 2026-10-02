using System;
using System.Collections.Generic;
using DOL.Database;

namespace DOL.GS
{
    public static class FrontierSupplyMerchantPolicy
    {
        private static readonly HashSet<string> SiegeKits = new(StringComparer.OrdinalIgnoreCase)
        {
            "deploy_heavy_siege_ram",
            "deploy_siege_ram", "deploy_siege_ram2", "deploy_siege_ram3",
            "deploy_siege_trebuchet", "deploy_siege_trebuchet2", "deploy_siege_trebuchet3",
            "deploy_siege_ballista", "deploy_siege_ballista2", "deploy_siege_ballista3",
            "deploy_siege_catapult", "deploy_siege_catapult2", "deploy_siege_catapult3"
        };

        public static string Label(string list) => list switch
        {
            "SiegeMerchantAlb" or "SiegeMerchantMid" or "SiegeMerchantHib" => "Siege Equipment",
            "OFMerchant_Alb" or "OFMerchant_Mid" or "OFMerchant_Hib" or
            "OFMerchant_Alb_Home" or "OFMerchant_Mid_Home" or
            "OFMerchant_Alb_HomeHib" or "OFMerchant_Alb_HomeMid" or
            "OFMerchant_Mid_HomeHib" or "OFMerchant_Mid_HomeAlb" or
            "OFMerchant_Hib_Home" or "OFMerchant_Hib_HomeAlb" or "OFMerchant_Hib_HomeMid" => "Teleport Medallions",
            _ => null
        };

        public static void ApplyLabel(GameMerchant merchant)
        {
            string label = Label(merchant?.TradeItems?.ItemsListID);
            if (label == null) return;
            merchant.GuildName = label;
            merchant.Flags &= ~GameNPC.eFlags.DONTSHOWNAME;
        }

        // Only ordinary, purchasable siege kits bypass the no-drop sale gate.
        // Preserve no-drop flags so automatic inventory cleanup keeps needed rams.
        public static bool CanSell(DbInventoryItem item) => item != null &&
            (item.IsDropable || item.IsTradable && item.Price > 0 && SiegeKits.Contains(item.Id_nb ?? ""));
    }
}
