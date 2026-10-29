using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cyclop.Core.Shelf;

namespace Cyclop.Windows.Shelf;

/// <summary>WIC-backed image normalization with bounded dimensions and SHA-256 over decoded BGRA pixels.</summary>
public sealed class WpfImageFingerprint : IImageFingerprint
{
    public const long MaxPixels = 40_000_000;
    public byte[] GetPixelHash(ReadOnlyMemory<byte> imageBytes) => Hash(Decode(imageBytes));
    public byte[] GetPixelHashFromFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Image exceeds the processing limit.");
        var expectedLength = stream.Length;
        var bytes = new byte[checked((int)expectedLength)];
        stream.ReadExactly(bytes);
        if (stream.Length != expectedLength) throw new InvalidDataException("Image changed during fingerprinting.");
        return Hash(Decode(bytes));
    }
    public byte[] EncodeAsPng(ReadOnlyMemory<byte> imageBytes)
    {
        return EncodeBitmapAsPng(Decode(imageBytes));
    }
    public byte[] EncodeBitmapAsPng(BitmapSource bitmap)
    {
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaxPixels) throw new InvalidDataException("Image dimensions exceed the processing limit.");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Encoded image exceeds the storage limit.");
        return stream.ToArray();
    }

    public BitmapSource Decode(ReadOnlyMemory<byte> imageBytes)
    {
        if (imageBytes.Length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Image exceeds the processing limit.");
        var encodedBytes = imageBytes.ToArray();
        using (var metadataStream = new MemoryStream(encodedBytes, writable: false))
        {
            var metadataDecoder = BitmapDecoder.Create(metadataStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
            var metadataFrame = metadataDecoder.Frames.FirstOrDefault() ?? throw new InvalidDataException("Image has no frame.");
            if ((long)metadataFrame.PixelWidth * metadataFrame.PixelHeight > MaxPixels)
                throw new InvalidDataException("Image dimensions exceed the processing limit.");
        }
        using var stream = new MemoryStream(encodedBytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault() ?? throw new InvalidDataException("Image has no frame.");
        if ((long)frame.PixelWidth * frame.PixelHeight > MaxPixels) throw new InvalidDataException("Image dimensions exceed the processing limit.");
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static byte[] Hash(BitmapSource bitmap)
    {
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        var metadata = BitConverter.GetBytes(bitmap.PixelWidth).Concat(BitConverter.GetBytes(bitmap.PixelHeight)).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(metadata); hash.AppendData(pixels);
        CryptographicOperations.ZeroMemory(pixels);
        return hash.GetHashAndReset();
    }
}
