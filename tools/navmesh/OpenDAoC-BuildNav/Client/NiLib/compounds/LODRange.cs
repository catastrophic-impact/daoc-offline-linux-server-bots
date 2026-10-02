using System.IO;

namespace MNL {
  public class LODRange {
    public float NearExtent;
    public float FarExtent;
    public uint[] UnkownInts;

    public LODRange(NiFile file, BinaryReader reader) {
      NearExtent = reader.ReadSingle();
      FarExtent = reader.ReadSingle();

      if (file.Version <= eNifVersion.VER_3_1) {
        UnkownInts = new[]
            {
                        reader.ReadUInt32(),
                        reader.ReadUInt32(),
                        reader.ReadUInt32(),
                    };
      }
    }
  }
}
