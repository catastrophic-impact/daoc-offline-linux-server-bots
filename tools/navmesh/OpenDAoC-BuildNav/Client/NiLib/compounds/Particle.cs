using System.IO;
using OpenTK;

namespace MNL {
  public class Particle {
    public Vector3 Velocity;
    public Vector3 UnkownVector;
    public float Lifetime;
    public float Lifespan;
    public float Timestamp;
    public ushort UnkownShort;
    public ushort VertexID;

    public Particle(NiFile file, BinaryReader reader) {
      Velocity = reader.ReadVector3();
      UnkownVector = reader.ReadVector3();
      Lifetime = reader.ReadSingle();
      Lifespan = reader.ReadSingle();
      Timestamp = reader.ReadSingle();
      UnkownShort = reader.ReadUInt16();
      VertexID = reader.ReadUInt16();

    }
  }
}