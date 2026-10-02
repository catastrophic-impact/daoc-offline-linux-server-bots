using DOL.GS.PacketHandler;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_SluaghbinderTooltipIdentity
    {
        [TestCase(59000)] [TestCase(59024)] [TestCase(59069)] [TestCase(59076)]
        [TestCase(59105)] [TestCase(59109)] [TestCase(59110)] [TestCase(59111)]
        public void EverySluaghbinderSpellUsesItsOwnTooltip(int spellId)
        {
            Assert.That(PacketLib1110.UsesOwnTooltipIdentity(spellId), Is.True);
        }

        [TestCase(1706)] [TestCase(3154)] [TestCase(58999)] [TestCase(59112)]
        public void StockSpellsKeepTheIconAsTooltip(int spellId)
        {
            Assert.That(PacketLib1110.UsesOwnTooltipIdentity(spellId), Is.False);
        }
    }
}
