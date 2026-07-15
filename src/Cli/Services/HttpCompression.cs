using System.IO.Compression;

namespace SmokeSolver.Cli;

/// <summary>Whole-buffer gzip/brotli helpers for pre-compressed responses.</summary>
public static class HttpCompression
{
    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    public static byte[] Brotli(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize))
        {
            brotli.Write(data);
        }
        return output.ToArray();
    }
}
