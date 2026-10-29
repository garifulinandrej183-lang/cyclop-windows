namespace Cyclop.Core.Data;

/// <summary>Protects persisted user data using the current Windows user's key.</summary>
public interface IDataProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}
