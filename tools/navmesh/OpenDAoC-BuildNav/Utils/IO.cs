using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace CEM.Utils {
  /// <summary>
  /// IO Helper
  /// </summary>
  internal static class IO {
    /// <summary>
    /// Builds a path from single directory components
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    public static string MakePath(params string[] args) {
      return args.Aggregate(Path.Combine);
    }

    private static bool IsGZip(string file) {
      return file.ToLower().EndsWith(".gz");
    }

    /// <summary>
    /// GZip's all the specified files
    /// </summary>
    /// <param name="files"></param>
    public static void GZipAll(params string[] files) {
      if (files.Length > 0)
        Log.Debug("Zipping {0} files ...", files.Length);
      foreach (string file in files) {
        if (IsGZip(file))
          continue;
        using (Stream input = OpenRead(file)) {
          using (Stream output = OpenWrite(file + ".gz")) {
            input.CopyTo(output);
            output.Flush();
          }
        }
        File.Delete(file);
      }
    }

    /// <summary>
    /// Temporarily unzips a file
    /// </summary>
    public class Unzip : IDisposable {
      /// <summary>
      /// Unzipped filename
      /// </summary>
      public string UnzippedFile { get; private set; }

      /// <summary>
      /// Temporarily unzips the specified file
      /// </summary>
      /// <param name="file"></param>
      public Unzip(string file) {
        UnzippedFile = Path.GetTempFileName();
        using (Stream output = OpenWrite(UnzippedFile),
                      input = OpenRead(file))
          input.CopyTo(output);
      }

      public void Dispose() {
        try {
          File.Delete(UnzippedFile);
        }
        catch (Exception ex) {
          Log.Error(ex);
        }
      }
    }

    /// <summary>
    /// Opens the file for writing. Intelligent file detection.
    /// </summary>
    /// <param name="dest"></param>
    /// <returns></returns>
    public static Stream OpenWrite(string dest) {
      string dir = Path.GetDirectoryName(dest);
      if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);
      Stream stream = File.OpenWrite(dest);
      if (IsGZip(dest)) stream = new GZipStream(stream, CompressionMode.Compress);
      return stream;
    }

    /// <summary>
    /// Opens the file for writing. Intelligent file detection.
    /// </summary>
    /// <param name="dest"></param>
    /// <returns></returns>
    public static Stream OpenRead(string dest) {
      if (!File.Exists(dest))
        return null;
      Stream stream = File.OpenRead(dest);
      if (IsGZip(dest)) stream = new GZipStream(stream, CompressionMode.Decompress);
      return stream;
    }
  }
}