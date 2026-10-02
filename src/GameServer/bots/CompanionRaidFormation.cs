using System;

namespace DOL.GS
{
    public static class CompanionRaidFormation
    {
        public static (double Angle, double Radius) Slot(int ordinal, bool interior)
        {
            ordinal = Math.Clamp(ordinal, 0, 78);
            int ring = ordinal < 10 ? 0 : ordinal < 23 ? 1 : ordinal < 39 ? 2 : ordinal < 58 ? 3 : 4;
            int index = ordinal - (ring == 0 ? 0 : ring == 1 ? 10 : ring == 2 ? 23 : ring == 3 ? 39 : 58);
            int count = ring == 0 ? 10 : ring == 1 ? 13 : ring == 2 ? 16 : ring == 3 ? 19 : 21;
            // World-fixed angles avoid marching the entire raid around whenever
            // its owner turns the camera. Mesh projection is applied by caller.
            return (index * Math.PI * 2 / count + ring * 0.19,
                interior ? 90 + ring * 55 : 110 + ring * 70);
        }
    }
}
