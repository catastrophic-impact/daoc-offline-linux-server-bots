using DOL.GS;
using DOL.GS.PacketHandler;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_HelmetAppearanceCompatibility
    {
        [TestCase(840, 0, 0)]
        [TestCase(840, 1, 1)]
        [TestCase(840, 2, 0)]
        [TestCase(840, 3, 0)]
        [TestCase(840, 5, 5)]
        [TestCase(827, 0, 0)]
        [TestCase(827, 1, 1)]
        [TestCase(827, 2, 0)]
        [TestCase(827, 3, 0)]
        [TestCase(827, 7, 7)]
        [TestCase(440, 2, 0)]
        [TestCase(440, 3, 0)]
        [TestCase(2849, 3, 0)]
        [TestCase(839, 3, 3)]
        [TestCase(838, 2, 2)]
        public void OnlyConfirmedBrokenCoifExtensionsAreRemapped(int model, byte itemExtension, byte visibleExtension)
        {
            Assert.That(HelmetAppearanceCompatibility.VisibleExtension(
                (int)eInventorySlot.HeadArmor, model, itemExtension), Is.EqualTo(visibleExtension));
        }

        [Test]
        public void NonHelmetEquipmentIsUnchanged()
        {
            Assert.That(HelmetAppearanceCompatibility.VisibleExtension(
                (int)eInventorySlot.TorsoArmor, 840, 3), Is.EqualTo(3));
        }
    }
}
