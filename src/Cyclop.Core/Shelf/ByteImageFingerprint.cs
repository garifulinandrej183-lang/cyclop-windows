using System.Security.Cryptography;

namespace Cyclop.Core.Shelf;

/// <summary>Portable fallback. Windows UI should inject WpfImageFingerprint for decoded-pixel deduplication.</summary>
public sealed class ByteImageFingerprint : IImageFingerprint
{
    public byte[] GetPixelHash(ReadOnlyMemory<byte> imageBytes) => SHA256.HashData(imageBytes.Span);
    public byte[] GetPixelHashFromFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Image exceeds the processing limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.Length != bytes.Length) throw new InvalidDataException("Image changed during fingerprinting.");
        return SHA256.HashData(bytes);
    }
    public byte[] EncodeAsPng(ReadOnlyMemory<byte> imageBytes) => imageBytes.ToArray();
}
