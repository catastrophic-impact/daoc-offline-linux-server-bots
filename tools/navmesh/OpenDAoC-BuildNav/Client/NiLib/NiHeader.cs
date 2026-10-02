using System.Text.RegularExpressions;

namespace MNL {
    public class NiHeader {
        private static readonly Regex VersionFromHeaderString = new(
            @"Version\s+(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public string VersionString;
        public eNifVersion Version = eNifVersion.VER_UNSUPPORTED;
        public byte EndianType = 1;
        public uint UserVersion = 0;
        public uint UserVersion2 = 0;
        public NiString[] BlockTypes;
        public ushort[] BlockTypeIndex;
        public uint[] BlockSizes;
        public uint NumBlocks;
        public uint UnkownInt = 0;
        public string[] Copyright;

        public NiHeader(NiFile file, BinaryReader reader)
        {
            VersionString = ReadLine(reader);

            if (!TryParseVersionFromHeaderString(VersionString, out var stringVersion))
            {
                throw new InvalidOperationException($"Unrecognized NIF header string '{VersionString}'. (File: {file.FileName})");
            }

            // Versions through 3.1 use three copyright lines and no binary Version field.
            // Reading the next 4 bytes as Version was mis-parsing "Nume..." (Numerical Design...)
            // as 0x656D754E and reporting it as VER_20_0_0_4 via a >= check.
            if (stringVersion <= eNifVersion.VER_3_1)
            {
                Copyright = new string[3];
                for (var i = 0; i < 3; i++)
                {
                    Copyright[i] = ReadLine(reader);
                }

                Version = stringVersion;
                throw new InvalidOperationException($"{Version} not supported. (File: {file.FileName})");
            }

            var ver = reader.ReadUInt32();
            if (!IsKnownVersion(ver))
            {
                throw new InvalidOperationException($"Unknown NIF version 0x{ver:X8} (header string: '{VersionString}'). (File: {file.FileName})");
            }

            Version = (eNifVersion)ver;

            // Standard Gamebryo 20.0.0.4+ inserts a 1-byte Endian Type (0=BE, 1=LE) after Version.
            if (Version >= eNifVersion.VER_20_0_0_4)
            {
                EndianType = reader.ReadByte();

                if (EndianType != 0 && EndianType != 1)
                {
                    throw new InvalidOperationException($"Invalid EndianType 0x{EndianType:X2} found. Expected 0 (BE) or 1 (LE). (File: {file.FileName})");
                }
            }

            if (Version >= eNifVersion.VER_10_1_0_0)
            {
                UserVersion = reader.ReadUInt32();
            }

            if (Version >= eNifVersion.VER_3_3_0_13)
            {
                NumBlocks = reader.ReadUInt32();
            }

            if (Version >= eNifVersion.VER_10_1_0_0 && (UserVersion == 10 || UserVersion == 11))
            {
                UserVersion2 = reader.ReadUInt32();
            }

            if (Version == eNifVersion.VER_20_0_0_5)
            {
                throw new InvalidOperationException($"{nameof(eNifVersion.VER_20_0_0_5)} not supported. (File: {file.FileName})");
            }

            if (Version == eNifVersion.VER_10_0_1_2)
            {
                throw new InvalidOperationException($"{nameof(eNifVersion.VER_10_0_1_2)} not supported. (File: {file.FileName})");
            }

            if (Version >= eNifVersion.VER_10_1_0_0 && (UserVersion == 10 || UserVersion == 11))
            {
                throw new InvalidOperationException($"Bethesda NIF (UserVersion={UserVersion}) not supported. (File: {file.FileName})");
            }

            if (Version >= eNifVersion.VER_10_0_1_0)
            {
                var numBlockTypes = reader.ReadUInt16();
                BlockTypes = new NiString[numBlockTypes];
                for (var i = 0; i < numBlockTypes; i++)
                {
                    BlockTypes[i] = new NiString(file, reader);
                }
                BlockTypeIndex = new ushort[NumBlocks];
                for (var i = 0; i < NumBlocks; i++)
                {
                    BlockTypeIndex[i] = reader.ReadUInt16();
                }
            }

            if (Version >= eNifVersion.VER_20_2_0_7)
            {
                throw new InvalidOperationException($"{Version} not supported. (File: {file.FileName})");
            }

            if (Version >= eNifVersion.VER_20_1_0_3)
            {
                throw new InvalidOperationException($"{Version} not supported. (File: {file.FileName})");
            }

            if (Version >= eNifVersion.VER_10_0_1_0)
            {
                UnkownInt = reader.ReadUInt32();
            }
        }

        private static string ReadLine(BinaryReader reader)
        {
            var startOffset = reader.BaseStream.Position;
            var strLen = 0;
            while (reader.ReadByte() != 0x0A)
                strLen++;

            reader.BaseStream.Position = startOffset;
            var line = new string(reader.ReadChars(strLen));
            reader.ReadByte(); // skip 0x0A
            return line;
        }

        /// <summary>
        /// Parses the version text from the NIF header line
        /// (e.g. "NetImmerse File Format, Version 3.0").
        /// </summary>
        internal static bool TryParseVersionFromHeaderString(string header, out eNifVersion version)
        {
            version = eNifVersion.VER_INVALID;
            if (string.IsNullOrEmpty(header))
                return false;

            var m = VersionFromHeaderString.Match(header);
            if (!m.Success)
                return false;

            // Special case: "3.03" is 0x03000300, not 3.3.0.0.
            if (m.Groups[1].Value == "3" && m.Groups[2].Value == "03" && !m.Groups[3].Success)
            {
                version = eNifVersion.VER_3_03;
                return true;
            }

            var a = uint.Parse(m.Groups[1].Value);
            var b = uint.Parse(m.Groups[2].Value);
            var c = m.Groups[3].Success ? uint.Parse(m.Groups[3].Value) : 0u;
            var d = m.Groups[4].Success ? uint.Parse(m.Groups[4].Value) : 0u;
            var ver = (a << 24) | (b << 16) | (c << 8) | d;
            version = (eNifVersion)ver;
            return true;
        }

        private static bool IsKnownVersion(uint ver) {
            if (ver == (uint)eNifVersion.VER_UNSUPPORTED || ver == (uint)eNifVersion.VER_INVALID)
                return false;
            return Enum.IsDefined(typeof(eNifVersion), ver);
        }
    }
}
