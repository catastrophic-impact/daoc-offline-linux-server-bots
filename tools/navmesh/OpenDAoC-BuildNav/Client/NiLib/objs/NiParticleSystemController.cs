using System.IO;
using OpenTK;
using OpenTK.Graphics;

namespace MNL {
  public class NiParticleSystemController : NiTimeController {
    public uint OldSpeed = 0;
    public float Speed = 0;
    public float RandomSpeed = 0;
    public float VerticalDirection = 0;
    public float VerticalAngle = 0;
    public float HorizontalDirection = 0;
    public float HorizontalAngle = 0;
    public Vector3 UnkownNormal;
    public Color4 UnkownColor;
    public float Size = 0;
    public float EmitStartTime = 0;
    public float EmitStopTime = 0;
    public byte UnkownByte = 0;
    public uint OldEmitRate = 0;
    public float EmitRate = 0;
    public float Lifetime = 0;
    public float LifetimeRandom = 0;
    public ushort EmitFlags = 0;
    public Vector3 StartRandom;
    public NiRef<NiObject> Emitter;
    public Vector3 ParticleVelocity;
    public Vector3 ParticleUnkownVector;
    public float ParticleLifeTime;
    public NiRef<NiObject> ParticleLink;
    public uint ParticleTimestamp;
    public ushort ParticleUnkownShort;
    public ushort ParticleVertexId;
    public ushort NumParticles;
    public ushort NumValid;
    public Particle[] Particles;
    public NiRef<NiObject> UnkownRef;
    public NiRef<NiParticleModifier> ParticleExtra;
    public NiRef<NiObject> UnkownRef2;
    public byte Trailer;
    public NiRef<NiColorData> ColorData;
    public float UnkownFloat1;
    public float[] UnkownFloats2;


    public NiParticleSystemController(NiFile file, BinaryReader reader)
        : base(file, reader) {
      if (Version <= eNifVersion.VER_3_1) {
        OldSpeed = reader.ReadUInt32();
      }

      if (Version >= eNifVersion.VER_3_3_0_13) {
        Speed = reader.ReadSingle();
      }

      RandomSpeed = reader.ReadSingle();
      VerticalDirection = reader.ReadSingle();
      VerticalAngle = reader.ReadSingle();
      HorizontalDirection = reader.ReadSingle();
      HorizontalAngle = reader.ReadSingle();
      UnkownNormal = reader.ReadVector3();
      UnkownColor = reader.ReadColor4();
      Size = reader.ReadSingle();
      EmitStartTime = reader.ReadSingle();
      EmitStopTime = reader.ReadSingle();

      if (Version >= eNifVersion.VER_4_0_0_2) {
        UnkownByte = reader.ReadByte();
      }

      if (Version <= eNifVersion.VER_3_1) {
        OldEmitRate = reader.ReadUInt32();
      }

      if (Version >= eNifVersion.VER_3_3_0_13) {
        EmitRate = reader.ReadSingle();
      }

      Lifetime = reader.ReadSingle();
      LifetimeRandom = reader.ReadSingle();

      if (Version >= eNifVersion.VER_4_0_0_2) {
        EmitFlags = reader.ReadUInt16();
      }

      StartRandom = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
      Emitter = new NiRef<NiObject>(reader);

      if (Version >= eNifVersion.VER_4_0_0_2) {
        reader.ReadUInt16();
        reader.ReadSingle();
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt16();
      }

      if (Version <= eNifVersion.VER_3_1) {
        ParticleVelocity = reader.ReadVector3();
        ParticleUnkownVector = reader.ReadVector3();
        ParticleLifeTime = reader.ReadSingle();
        ParticleLink = new NiRef<NiObject>(reader);
        ParticleTimestamp = reader.ReadUInt32();
        ParticleUnkownShort = reader.ReadUInt16();
        ParticleVertexId = reader.ReadUInt16();
      }

      if (Version >= eNifVersion.VER_4_0_0_2) {
        NumParticles = reader.ReadUInt16();
        NumValid = reader.ReadUInt16();
        Particles = new Particle[NumParticles];
        for (var i = 0; i < NumParticles; i++) {
          Particles[i] = new Particle(file, reader);
        }
        UnkownRef = new NiRef<NiObject>(reader);
      }

      ParticleExtra = new NiRef<NiParticleModifier>(reader);
      UnkownRef2 = new NiRef<NiObject>(reader);
      if (Version >= eNifVersion.VER_4_0_0_2) {
        Trailer = reader.ReadByte();
      }

      if (Version <= eNifVersion.VER_3_1) {
        ColorData = new NiRef<NiColorData>(reader);
        UnkownFloat1 = reader.ReadSingle();
        UnkownFloats2 = reader.ReadFloatArray(ParticleUnkownShort);
      }
    }
  }
}