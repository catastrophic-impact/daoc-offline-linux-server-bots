using System.Collections.Generic;

namespace DOL.GS.PacketHandler
{
    /// <summary>
    /// Presentation-only compatibility for helmet variants that hide the wearer's face in the legacy client.
    /// Item templates, inventory records, and armor properties retain their original extension.
    /// </summary>
    public static class HelmetAppearanceCompatibility
    {
        // Every client helmet object built on the Hibernian "Hib Helm 3" mesh (items.csv 407, H_helm3,
        // Head # 4, alternates 398/404): the scale coif 840, the studded/amber cailiocht cap 827, the
        // leather helm 440 and their guard/possessed/good variants. They render correctly as extension 0,
        // while the extension 2 and 3 variants hide the entire face.
        private static readonly HashSet<int> HibHelm3Models =
        [
            440, 827, 837, 840, 1203, 1207, 1211,
            2769, 2775, 2781, 2787, 2831, 2837, 2843, 2849
        ];

        public static byte VisibleExtension(int slot, int model, byte extension)
        {
            if (slot == (int)eInventorySlot.HeadArmor && HibHelm3Models.Contains(model) &&
                (extension == 2 || extension == 3))
                return 0;

            return extension;
        }
    }
}
