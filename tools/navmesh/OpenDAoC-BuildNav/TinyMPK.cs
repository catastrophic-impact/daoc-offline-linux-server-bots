using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CEM.Client {
  /// <summary>
  /// Lightweight implementation of the MPAK Library.
  /// (c) mlinder
  /// WARNING: Untested code
  /// </summary>
  internal class TinyMPK {
    private const int MAX_CACHE_COUNT = 4;

    /// <summary>
    /// Simple walk-around for our broken MPK loader that cannot extract single files from an MPK.
    /// Idea: Keep recently used MPKs in cache
    /// </summary>
    private static LinkedList<TinyMPK> _rollingMPKCache = new LinkedList<TinyMPK>();

    #region Propertys/Variables

    /// <summary>
    /// The 'MPAK' string as an uint
    /// </summary>
    private const uint MAGIC_KEY = ('M') + ('P' << 8) + ('A' << 16) + ('K' << 24); //"MPAK"

    /// <summary>
    /// Size of a single Directory Entry
    /// </summary>
    private const int FILE_ENTRY_SIZE = 0x11C;

    /// <summary>
    /// Encoding used in MPAKs
    /// </summary>
    private static readonly Encoding _encoding = Encoding.UTF8;

    private readonly List<MPKFileEntry> _entries = new List<MPKFileEntry>();

    /// <summary>
    /// The internal Archive Name (DAoC Client uses this, so change this if you change the filename)
    /// </summary>
    public string ArchiveName { get; private set; }

    /// <summary>
    /// MPK Checksum
    /// </summary>
    /// <value>The checksum.</value>
    public uint Checksum { get; private set; }

    /// <summary>
    /// Array Containing all Files in the MPAK (as MPAKFileEntry's)
    /// </summary>
    public MPKFileEntry[] Files {
      get { return _entries.ToArray(); }
    }

    #endregion

    private TinyMPK() {}

    #region Save/Load

    /// <summary>
    /// Loads a .mpk file
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    public static TinyMPK FromFile(string file) {
      var mpk = new TinyMPK();
      return mpk.Load(file);
    }

    /// <summary>
    /// Loads the MPAK from the specified file into Memory
    /// </summary>
    /// <param name="file">Filepath</param>
    /// <returns>eMPAKError.None if successfull, otherwise the Error</returns>
    public TinyMPK Load(string file) {
      try {
        _entries.Clear();

        if (!File.Exists(file))
          throw new FileNotFoundException("MPK was not found", file);

        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read))
          return Load(fs);
      }
      catch (Exception ex) {
        throw new IOException("MPK Error in file " + file+ ": "+ ex.Message, ex);
      }
    }

    /// <summary>
    /// Loads the MPAK from Stream into Memory
    /// </summary>
    /// <param name="stream">MPAK Stream</param>
    /// <returns>eMPAKError.None if successfull, otherwise the Error</returns>
    public TinyMPK Load(Stream stream) {
      var br = new BinaryReader(stream);

      if (br.ReadUInt32() != MAGIC_KEY) //"MPAK" string
      {
        throw new IOException("MPK file is not a .mpk");
      }

      br.ReadByte(); //unknown; always 2

      uint checksum;
      uint dirsize;
      uint namesize;
      uint filecount;

      #region Header

      {
        var header = new byte[16];

        for (int a = 0; a < header.Length; a++)
          header[a] = (byte) (br.ReadByte() ^ a);

        Checksum = checksum = (uint) (header[0] + (header[1] << 8) + (header[2] << 16) + (header[3] << 24));
        dirsize = (uint) (header[4] + (header[5] << 8) + (header[6] << 16) + (header[7] << 24));
        namesize = (uint) (header[8] + (header[9] << 8) + (header[10] << 16) + (header[11] << 24));
        filecount = (uint) (header[12] + (header[13] << 8) + (header[14] << 16) + (header[15] << 24));
      }

      // Check if a MPK with that checksum has already been loaded
      lock (_rollingMPKCache) {
        foreach (var cached in _rollingMPKCache) {
          if (cached.Checksum == checksum) { // same mpk
            // Promote var to LRU
            _rollingMPKCache.Remove(cached);
            _rollingMPKCache.AddFirst(cached);
            return cached; // return cached copy
          }
        }
      }

      #endregion

      #region Name

      {
        byte[] input = br.ReadBytes((int) namesize);
        byte[] name = Decompress(input);
        ArchiveName = _encoding.GetString(name, 0, name.Length);
      }

      #endregion

      #region Files

      {
        byte[] input = br.ReadBytes((int) dirsize);
        byte[] datas = br.ReadBytes((int) (br.BaseStream.Length - br.BaseStream.Position));

        //Do a CRC Check

        if (Crc32.Compute(input) != checksum) //invalid checksum
          throw new IOException("Directory Structure CRC mismatch");

        byte[] dirs = Decompress(input);

        //lets go through the entry
        for (int a = 0; a < filecount; a++) {
          var ms = new MemoryStream(dirs, a*FILE_ENTRY_SIZE, FILE_ENTRY_SIZE);
          var r = new BinaryReader(ms);

          string name = GetNullString(r.ReadBytes(256));
          uint ts = r.ReadUInt32();
          r.ReadUInt32(); //unk, mostly 4
          r.ReadUInt32(); //offset in mem
          uint dsize = r.ReadUInt32();
          uint offset = r.ReadUInt32();
          uint csize = r.ReadUInt32();
          uint ccrc = r.ReadUInt32();

          var compressedData = new byte[csize];
          Array.Copy(datas, offset, compressedData, 0, csize);

          //Do a crc check
          if (Crc32.Compute(compressedData) != ccrc)
            throw new IOException("File CRC mismatch at " + name);

          byte[] data = Decompress(compressedData);
          _entries.Add(new MPKFileEntry(name, ts, data));

          r.Close();
          ms.Dispose();
        }
      }

      #endregion

      //Sort();

      // Add to cache
      lock (_rollingMPKCache) {
        _rollingMPKCache.AddFirst(this);
        if (_rollingMPKCache.Count > MAX_CACHE_COUNT)
          _rollingMPKCache.RemoveLast();
      }
      return this;
    }

    /// <summary>
    /// Gets the MPAKFileEntry belonging to the specified Name
    /// </summary>
    /// <param name="name">Filename</param>
    /// <returns>MPAKFileEntry</returns>
    public MPKFileEntry GetFile(string name) {
      foreach (var e in _entries) {
        if (e.Name.ToLower().Equals(name.ToLower()))
          return e;
      }

      return null;
    }

    /// <summary>
    /// Sorts the Entrys of the MPAK
    /// </summary>
    private void Sort() {
      _entries.Sort(new MPKEntrySorter());
    }

    #endregion

    #region Helpers

    // Doesn't seem to work.
    private static byte[] Decompress(byte[] src, uint dsize) {
      var res = new byte[dsize];
      var ms = new MemoryStream(src, false);
      ms.Position = 2; // skip first 2 bytes (zlib)
      using (var ds = new DeflateStream(ms, CompressionMode.Decompress)) {
        int left = res.Length;
        while (left > 0)
          left -= ds.Read(res, 0, left);
      }
      return res;
    }

    private static byte[] Decompress(byte[] src) {
      var res = new List<byte>();
      var buf = new byte[2048];
      var ms = new MemoryStream(src, false);
      ms.Position = 2; // skip first 2 bytes (zlib)
      using (var ds = new DeflateStream(ms, CompressionMode.Decompress)) {
        int read;
        do {
          read = ds.Read(buf, 0, buf.Length);
          byte[] buf2 = buf;
          if (read != buf.Length) {
            buf2 = new byte[read];
            Array.Copy(buf, buf2, read);
          }
          res.AddRange(buf2);
        } while (read > 0);
      }
      return res.ToArray();
    }

    private static string GetNullString(byte[] src) {
      for (int a = 0; a < src.Length; a++) {
        if (src[a] == 0)
          return _encoding.GetString(src, 0, a);
      }

      return _encoding.GetString(src);
    }

    private class MPKEntrySorter : IComparer<MPKFileEntry> {
      #region IComparer<MPKFileEntry> Members

      public int Compare(MPKFileEntry x, MPKFileEntry y) {
        return (x.Name.CompareTo(y.Name));
      }

      #endregion
    }

    #endregion
  }

  /// <summary>
  /// MPAKFileEntry
  /// A Entry in the MPAK Archive
  /// </summary>
  internal class MPKFileEntry {
    /// <summary>
    /// Creates a new MPAK Entry with the specified parameters
    /// </summary>
    /// <param name="name">Filename</param>
    /// <param name="timestamp">Timestamp</param>
    /// <param name="data">Content</param>
    public MPKFileEntry(string name, uint timestamp, byte[] data) {
      Name = name;
      Timestamp = timestamp;
      Data = data;
    }

    /// <summary>
    /// Name of the file
    /// </summary>
    public string Name { get; private set; }

    /// <summary>
    /// Time when this file was added to the archive
    /// </summary>
    public uint Timestamp { get; private set; }

    /// <summary>
    /// Content
    /// </summary>
    public byte[] Data { get; private set; }

    /// <summary>
    /// Length/Size of Content
    /// </summary>
    public uint FileSize {
      get { return (uint) Data.Length; }
    }
  }
}