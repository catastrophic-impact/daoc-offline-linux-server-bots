using System.IO;

namespace MNL {
  public class NiKeyframeData : NiObject {
    public eKeyType KeyType;
    public QuatKey[] QuaternionKeys;
    public float UnkownFloat;
    public KeyGroup<FloatKey>[] Rotations;
    public KeyGroup<VecKey> Translations;
    public KeyGroup<FloatKey> Scales;

    public NiKeyframeData(NiFile file, BinaryReader reader)
        : base(file, reader) {
      var numRotationKeys = reader.ReadUInt32();
      if (numRotationKeys != 0) {
        KeyType = (eKeyType)reader.ReadUInt32();
      }

      if (KeyType != eKeyType.XYZ_ROTATION_KEY) {
        QuaternionKeys = new QuatKey[numRotationKeys];
        for (var c = 0; c < numRotationKeys; c++) {
          QuaternionKeys[c] = new QuatKey(reader, KeyType);
        }
      }

      if (Version <= eNifVersion.VER_10_1_0_0
          && KeyType == eKeyType.XYZ_ROTATION_KEY) {
        UnkownFloat = reader.ReadSingle(); // unknown float
      }

      if (KeyType == eKeyType.XYZ_ROTATION_KEY) {
        Rotations = new KeyGroup<FloatKey>[3];
        for (var i = 0; i < 3; i++) {
          Rotations[i] = new KeyGroup<FloatKey>(reader);
        }

      }

      Translations = new KeyGroup<VecKey>(reader);
      Scales = new KeyGroup<FloatKey>(reader);
    }
  }
}