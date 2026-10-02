using System;
using System.IO;
using OpenTK;

namespace MNL {
  public class NiAVObject : NiObjectNET {
    public ushort Flags;
    public ushort UnkownShort1;
    public Vector3 Translation;
    public Matrix4 Rotation;
    public float Scale;
    public Vector3 Velocity;
    public NiRef<NiProperty>[] Properties;
    public uint[] UnkownInts1;
    public byte UnkownByte;
    public bool HasBoundingBox;
    public NiRef<NiCollisionObject> CollisionObject;

    public NiNode Parent = null;

    public NiAVObject(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (File.Header.Version >= eNifVersion.VER_3_0) {
        Flags = reader.ReadUInt16();
      }

      if (File.Header.Version >= eNifVersion.VER_20_2_0_7 && File.Header.UserVersion == 11 && File.Header.UserVersion2 > 26) {
        UnkownShort1 = reader.ReadUInt16();
      }

      Translation = reader.ReadVector3();
      Rotation = reader.ReadMatrix33();
      Scale = reader.ReadSingle();

      if (File.Header.Version <= eNifVersion.VER_4_2_2_0) {
        Velocity = reader.ReadVector3();
      }

      if (File.Header.Version <= eNifVersion.VER_20_2_0_7 || File.Header.UserVersion <= 11) {

        var numProperties = reader.ReadUInt32();
        Properties = new NiRef<NiProperty>[numProperties];
        for (var i = 0; i < numProperties; i++) {
          Properties[i] = new NiRef<NiProperty>(reader.ReadUInt32());
        }
      }

      if (File.Header.Version <= eNifVersion.VER_2_3) {
        UnkownInts1 = new uint[4];
        for (var i = 0; i < 4; i++)
          UnkownInts1[i] = reader.ReadUInt32();

        UnkownByte = reader.ReadByte();
      }

      if (File.Header.Version >= eNifVersion.VER_3_0 && File.Header.Version <= eNifVersion.VER_4_2_2_0) {
        HasBoundingBox = reader.ReadBoolean();
        if (HasBoundingBox) {
          throw new Exception("Cannot read BoundingBoxes yet");
        }
      }

      if (File.Header.Version >= eNifVersion.VER_10_0_1_0) {
        CollisionObject = new NiRef<NiCollisionObject>(reader.ReadUInt32());
      }
    }
  }
}