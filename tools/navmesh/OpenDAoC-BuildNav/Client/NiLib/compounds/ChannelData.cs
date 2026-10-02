using System.IO;

namespace MNL {
  public class ChannelData {
    public eChannelType Type;
    public eChannelConvention Convention;
    public byte BitsPerChannel;
    public byte UnkownByte;

    public ChannelData(NiFile file, BinaryReader reader) {
      Type = (eChannelType)reader.ReadUInt32();
      Convention = (eChannelConvention)reader.ReadUInt32();
      BitsPerChannel = reader.ReadByte();
      UnkownByte = reader.ReadByte();
    }
  }
}
