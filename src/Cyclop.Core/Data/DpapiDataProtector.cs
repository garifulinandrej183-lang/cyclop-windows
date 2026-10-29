using System.Security.Cryptography;

namespace Cyclop.Core.Data;

/// <summary>DPAPI CurrentUser protection. Plaintext is never written to disk.</summary>
public sealed class DpapiDataProtector : IDataProtector
{
    private static readonly byte[] Entropy = "Cyclop.ClipboardHistory.v1"u8.ToArray();

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Clipboard history protection requires Windows DPAPI.");
        return ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Clipboard history protection requires Windows DPAPI.");
        return ProtectedData.Unprotect(ciphertext.ToArray(), Entropy, DataProtectionScope.CurrentUser);
    }
}
