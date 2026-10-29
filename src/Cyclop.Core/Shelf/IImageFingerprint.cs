namespace Cyclop.Core.Shelf;

/// <summary>Produces a stable hash of decoded image pixels, independent of PNG metadata/encoding.</summary>
public interface IImageFingerprint
{
    byte[] GetPixelHash(ReadOnlyMemory<byte> imageBytes);
    byte[] GetPixelHashFromFile(string path);
    byte[] EncodeAsPng(ReadOnlyMemory<byte> imageBytes);
}
