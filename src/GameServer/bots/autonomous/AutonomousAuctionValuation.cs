using System;

namespace DOL.GS
{
    /// <summary>
    /// Produces a seller recommendation from properties of a real looted item.
    /// It never creates items or coin and does not execute a transaction.
    /// </summary>
    public static class AutonomousAuctionValuation
    {
        public sealed record ItemFacts(
            int ItemLevel,
            int Quantity,
            int Quality,
            int ConditionPercent,
            double RarityScore,
            double StatUtilityScore,
            bool IsCraftingMaterial);

        public sealed record Price(long MinimumBidCopper, long BuyoutCopper);

        public static Price Recommend(ItemFacts item, double sellerDisposition = 0)
        {
            if (item.Quantity < 1) throw new ArgumentOutOfRangeException(nameof(item.Quantity));
            if (sellerDisposition is < -0.35 or > 0.50)
                throw new ArgumentOutOfRangeException(nameof(sellerDisposition));

            int level = Math.Clamp(item.ItemLevel, 1, 50);
            int quality = Math.Clamp(item.Quality, 1, 100);
            int condition = Math.Clamp(item.ConditionPercent, 1, 100);
            double rarity = Math.Clamp(item.RarityScore, 0, 10);
            double utility = Math.Max(0, item.StatUtilityScore);

            // Materials are valued per unit; equipment is valued once. The inputs
            // come from the actual item record and the result is only a recommendation.
            double baseCopper = item.IsCraftingMaterial
                ? (5 + level * 2.5 + rarity * 12) * item.Quantity
                : level * level * 8 + utility * 30 + rarity * rarity * 50;
            double wearFactor = item.IsCraftingMaterial ? 1 : (quality / 100d) * (0.45 + 0.55 * condition / 100d);
            double sellerFactor = 1 + sellerDisposition;
            long buyout = Math.Max(1, (long)Math.Round(baseCopper * wearFactor * sellerFactor, MidpointRounding.AwayFromZero));
            long minimumBid = Math.Max(1, (long)Math.Round(buyout * 0.75, MidpointRounding.AwayFromZero));
            return new Price(minimumBid, Math.Max(minimumBid, buyout));
        }
    }
}
