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
        [TestCase(838, 2, 0)]
        [TestCase(838, 1, 1)]
        [TestCase(1209, 2, 0)]
        [TestCase(835, 3, 3)]
        [TestCase(337, 2, 0)]   // rawhide starklaedar cap, Norse Helm 3 mesh
        [TestCase(337, 0, 0)]
        [TestCase(337, 3, 0)]
        [TestCase(834, 2, 0)]
        [TestCase(834, 3, 0)]   // fine alloy heavy starkakedja helm
        [TestCase(834, 0, 0)]
        [TestCase(834, 1, 1)]
        [TestCase(834, 5, 5)]
        [TestCase(2880, 2, 0)]
        [TestCase(1291, 3, 0)]  // fine alloy superior war circlet, Norse tiara mesh
        [TestCase(1291, 0, 0)]
        [TestCase(1291, 1, 1)]
        [TestCase(4465, 3, 0)]
        [TestCase(4465, 2, 2)]
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
