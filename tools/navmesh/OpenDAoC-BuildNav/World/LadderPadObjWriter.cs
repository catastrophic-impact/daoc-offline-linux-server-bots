using System.Globalization;
using CEM.Utils;
using OpenTK;

namespace CEM.World
{
    /// <summary>
    /// Appends horizontal virtual ladder pads to an existing zone .obj so Recast
    /// produces land polys for intermediate off-mesh endpoints.
    /// Pads are oriented in the ladder frame (width × depth along tangent × front).
    /// </summary>
    internal static class LadderPadObjWriter
    {
        public static int AppendPads(string objPath, IReadOnlyList<LadderLinkPlacer.LadderPad> pads, float scale)
        {
            if (pads.Count == 0)
                return 0;

            if (!File.Exists(objPath))
            {
                Log.Error($"Cannot append ladder pads; missing obj: {objPath}");
                return 0;
            }

            int vertexCount = CountVertices(objPath);
            int written = 0;

            using StreamWriter writer = new(objPath, true);
            writer.NewLine = "\n";

            for (int i = 0; i < pads.Count; i++)
            {
                LadderLinkPlacer.LadderPad pad = pads[i];
                writer.WriteLine($"g ladder_pad_{i}");

                (Vector3[] corners, bool tangentFrontRightHanded) = BuildTopCorners(pad);
                foreach (Vector3 c in corners)
                {
                    Vector3 v = c * scale;
                    writer.WriteLine(
                        "v {0} {1} {2}",
                        v.X.ToString(CultureInfo.InvariantCulture),
                        v.Z.ToString(CultureInfo.InvariantCulture),
                        v.Y.ToString(CultureInfo.InvariantCulture));
                }

                int b = vertexCount + 1;
                if (tangentFrontRightHanded)
                {
                    WriteFace(writer, b + 0, b + 2, b + 1);
                    WriteFace(writer, b + 0, b + 3, b + 2);
                }
                else
                {
                    WriteFace(writer, b + 0, b + 1, b + 2);
                    WriteFace(writer, b + 0, b + 2, b + 3);
                }

                vertexCount += corners.Length;
                written++;
            }

            Log.Normal($"Appended {written} ladder pad mesh(es) to {objPath}");
            return written;
        }

        public static void AppendLadderPadVolumes(string gset, List<LadderLinkPlacer.LadderPad> pads)
        {
            if (pads.Count == 0)
                return;

            using StreamWriter writer = new(gset, true);
            writer.NewLine = "\n";

            foreach (LadderLinkPlacer.LadderPad pad in pads)
            {
                Vector2 t = pad.Tangent;
                if (t.LengthSquared < 1e-6f)
                    t = Vector2.UnitX;

                Vector2 f = pad.Front;
                float proj = Vector2.Dot(f, t);
                f -= t * proj;
                if (f.Length > 1e-6f)
                    f.Normalize();
                else
                    f = new(-t.Y, t.X);

                float halfW = pad.Width * 0.5f;
                float halfD = pad.Depth * 0.5f;

                Vector2[] corners =
                [
                    new(pad.Center.X - t.X * halfW - f.X * halfD, pad.Center.Y - t.Y * halfW - f.Y * halfD),
                    new(pad.Center.X + t.X * halfW - f.X * halfD, pad.Center.Y + t.Y * halfW - f.Y * halfD),
                    new(pad.Center.X + t.X * halfW + f.X * halfD, pad.Center.Y + t.Y * halfW + f.Y * halfD),
                    new(pad.Center.X - t.X * halfW + f.X * halfD, pad.Center.Y - t.Y * halfW + f.Y * halfD),
                ];

                float minZ = (pad.Center.Z - 8f) * NavmeshMgr.CONVERSION_FACTOR;
                float maxZ = (pad.Center.Z + 8f) * NavmeshMgr.CONVERSION_FACTOR;
                int area = (int)GeomSetWriter.eAreas.Jump;

                writer.WriteLine($"v 4 {area} {minZ.ToString(CultureInfo.InvariantCulture)} {maxZ.ToString(CultureInfo.InvariantCulture)}");

                // Write vertices transformed to Recast coordinate space (X, Height, Y) scaled by CONVERSION_FACTOR.
                foreach (Vector2 corner in corners)
                {
                    float x = corner.X * NavmeshMgr.CONVERSION_FACTOR;
                    float y = corner.Y * NavmeshMgr.CONVERSION_FACTOR;

                    writer.WriteLine(
                        "{0} {1} {2}",
                        x.ToString(CultureInfo.InvariantCulture),
                        minZ.ToString(CultureInfo.InvariantCulture),
                        y.ToString(CultureInfo.InvariantCulture));
                }
            }

            Log.Debug($"Appended {pads.Count} ladder jump volumes to {gset}");
        }

        private static (Vector3[] Corners, bool RightHanded) BuildTopCorners(LadderLinkPlacer.LadderPad pad)
        {
            // Fallbacks just in case.
            Vector2 f = NormalizeOr(pad.Front, Vector2.UnitY);
            Vector2 t = NormalizeOr(pad.Tangent, new(-f.Y, f.X));

            // Keep tangent orthogonal to front (preserve the outward wall normal).
            float proj = Vector2.Dot(t, f);
            t -= f * proj;
            float tLen = t.Length;
            if (tLen > 1e-6f)
                t /= tLen;
            else
                t = new(-f.Y, f.X);

            // pad.Tangent's sign is not canonicalized upstream (exporter-dependent),
            // so (t, f) can come out either right- or left-handed depending on the
            // ladder. Record which, so the caller can pick the winding that
            // actually faces up rather than assuming a fixed handedness.
            float cross = t.X * f.Y - t.Y * f.X;
            bool rightHanded = cross >= 0f;

            float halfW = pad.Width * 0.5f;
            float halfD = pad.Depth * 0.5f;
            float zTop = pad.Center.Z;

            Vector3 c = pad.Center;

            Vector3[] corners =
            {
                new(c.X - t.X * halfW - f.X * halfD, c.Y - t.Y * halfW - f.Y * halfD, zTop),
                new(c.X + t.X * halfW - f.X * halfD, c.Y + t.Y * halfW - f.Y * halfD, zTop),
                new(c.X + t.X * halfW + f.X * halfD, c.Y + t.Y * halfW + f.Y * halfD, zTop),
                new(c.X - t.X * halfW + f.X * halfD, c.Y - t.Y * halfW + f.Y * halfD, zTop),
            };

            return (corners, rightHanded);
        }

        private static Vector2 NormalizeOr(Vector2 v, Vector2 fallback)
        {
            float len = v.Length;
            return len < 1e-6f ? fallback : v / len;
        }

        private static void WriteFace(TextWriter writer, int a, int b, int c)
        {
            writer.WriteLine("f {0} {1} {2}", a, b, c);
        }

        private static int CountVertices(string objPath)
        {
            int count = 0;
            foreach (string line in File.ReadLines(objPath))
            {
                if (line.Length >= 2 && line[0] == 'v' && line[1] == ' ')
                    count++;
            }

            return count;
        }
    }
}
