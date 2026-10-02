using System.IO;
using OpenTK;

namespace MNL {
  public class NiTexturingProperty : NiProperty {
    public ushort Flags = 0;
    public uint ApplyMode = 0;
    public uint TextureCount = 0;
    public TexDesc BaseTexture = null;
    public TexDesc DarkTexture = null;
    public TexDesc DetailTexture = null;
    public TexDesc GlossTexture = null;
    public TexDesc GlowTexture = null;
    public TexDesc BumpMapTexture = null;
    public TexDesc Decal0Texture = null;
    public TexDesc Decal1Texture = null;
    public TexDesc Decal2Texture = null;
    public TexDesc Decal3Texture = null;

    public uint Unkown1 = 0;

    public float BumpMapLumaScale = 0.0f;
    public float BumpMapLumaOffset = 0.0f;
    public Vector3 BumpMapMatrix;

    public TexDesc[] ShaderTextures;

    public NiTexturingProperty(NiFile file, BinaryReader reader) : base(file, reader) {
      if (Version <= eNifVersion.VER_10_0_1_2
          || Version >= eNifVersion.VER_20_1_0_3) {
        Flags = reader.ReadUInt16();
      }


      if (Version <= eNifVersion.VER_20_0_0_5) {
        ApplyMode = reader.ReadUInt32();
      }

      TextureCount = reader.ReadUInt32();

      if (reader.ReadBoolean()) {
        BaseTexture = new TexDesc(file, reader);
      }

      if (reader.ReadBoolean()) {
        DarkTexture = new TexDesc(file, reader);
      }

      if (reader.ReadBoolean()) {
        DetailTexture = new TexDesc(file, reader);
      }

      if (reader.ReadBoolean()) {
        GlossTexture = new TexDesc(file, reader);
      }

      if (reader.ReadBoolean()) {
        GlowTexture = new TexDesc(file, reader);
      }

      if (reader.ReadBoolean()) {
        BumpMapTexture = new TexDesc(file, reader);

        BumpMapLumaScale = reader.ReadSingle();
        BumpMapLumaOffset = reader.ReadSingle();
        BumpMapMatrix = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        reader.ReadSingle(); //its a 2x2 matrix


      }
      if (reader.ReadBoolean()) {
        Decal0Texture = new TexDesc(file, reader);
      }
      /*if (HasDecal1Texture)
      {
          Decal1Texture = new TextureHelper();
          Decal1Texture.Read(reader, File, "Decal1Texture");
      }
      if (HasDecal2Texture)
      {
          Decal2Texture = new TextureHelper();
          Decal2Texture.Read(reader, File, "Decal2Texture");
      }
      if (HasDecal3Texture)
      {
          Decal3Texture = new TextureHelper();
          Decal3Texture.Read(reader, File, "Decal3Texture");
      }*/

      if (Version >= eNifVersion.VER_10_0_1_0) {
        var numShaderTextures = reader.ReadUInt32();
        ShaderTextures = new TexDesc[numShaderTextures];
        for (var i = 0; i < numShaderTextures; i++) {
          bool isUsed = reader.ReadBoolean();
          if (isUsed) {
            ShaderTextures[i] = new TexDesc(file, reader);
            reader.ReadUInt32(); // MapID
          }
        }
      }
    }


  }
}