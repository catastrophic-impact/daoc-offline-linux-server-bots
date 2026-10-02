using System;

namespace DOL.GS;

public enum eAutonomousSiegePhase : byte
{
    Ready,
    RouteToMerchant,
    PurchaseKit,
    DeployRam,
    Hold
}

public static class AutonomousSiegePolicy
{
    public const string AlbionRamKitTemplateId = "deploy_siege_ram";
    public const string MidgardRamKitTemplateId = "deploy_siege_ram2";
    public const string HiberniaRamKitTemplateId = "deploy_siege_ram3";
    public const string AlbionMerchantListId = "SiegeMerchantAlb";
    public const string MidgardMerchantListId = "SiegeMerchantMid";
    public const string HiberniaMerchantListId = "SiegeMerchantHib";

    public static string RamKitTemplateId(eRealm realm) => realm switch
    {
        eRealm.Albion => AlbionRamKitTemplateId,
        eRealm.Midgard => MidgardRamKitTemplateId,
        eRealm.Hibernia => HiberniaRamKitTemplateId,
        _ => string.Empty
    };

    public static bool IsRamKit(eRealm realm, string templateId) =>
        string.Equals(templateId, RamKitTemplateId(realm), StringComparison.OrdinalIgnoreCase);

    public static bool IsRamKit(string templateId) =>
        string.Equals(templateId, AlbionRamKitTemplateId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(templateId, MidgardRamKitTemplateId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(templateId, HiberniaRamKitTemplateId, StringComparison.OrdinalIgnoreCase);

    public static string MerchantListId(eRealm realm) => realm switch
    {
        eRealm.Albion => AlbionMerchantListId,
        eRealm.Midgard => MidgardMerchantListId,
        eRealm.Hibernia => HiberniaMerchantListId,
        _ => string.Empty
    };

    // Merchant display names are ordinary character names in the live data.
    // The authoritative identity is the exact realm-specific trade list.
    public static bool IsSiegeMerchant(eRealm realm, string itemListId) =>
        !string.IsNullOrWhiteSpace(itemListId) &&
        string.Equals(itemListId, MerchantListId(realm), StringComparison.OrdinalIgnoreCase);

    public static bool CanSupplyRam(bool hasKit, long savedCopper, long kitPrice, bool hasBackpackSlot) =>
        hasKit || kitPrice > 0 && savedCopper >= kitPrice && hasBackpackSlot;

    public static bool IsDesignatedOperator(bool inDynamicGroup, bool isLeader) =>
        !inDynamicGroup || isLeader;

    public static eAutonomousSiegePhase ChoosePhase(bool hasKit, bool merchantFound, bool atMerchant, bool closedDoor, bool isOperator)
    {
        if (!isOperator)
            return eAutonomousSiegePhase.Hold;
        if (!hasKit)
            return merchantFound ? (atMerchant ? eAutonomousSiegePhase.PurchaseKit : eAutonomousSiegePhase.RouteToMerchant) : eAutonomousSiegePhase.Hold;
        return closedDoor ? eAutonomousSiegePhase.DeployRam : eAutonomousSiegePhase.Ready;
    }
}
