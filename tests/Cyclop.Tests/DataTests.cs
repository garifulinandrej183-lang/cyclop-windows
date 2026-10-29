using System.Security.Cryptography;
using System.Text;
using Cyclop.Core.Clipboard;
using Cyclop.Core.Data;
using Cyclop.Core.Shelf;

namespace Cyclop.Tests;

public sealed class DataTests
{
    [Fact]
    public async Task HistoryMemoryBudgetRejectsSingleOversizeAndRetainsNewestPayloads()
    {
        using var folder = new TemporaryDirectory();
        await using var history = new ClipboardHistoryService(new EncryptedClipboardRepository(folder.PathFor("budget.db"), new TestProtector()),
            new ClipboardCaptureOptions { ExplicitlyEnabled = true, MaxItems = 100, MaxTotalPayloadBytes = 1024 });
        await history.InitializeAsync();
        Assert.False(await history.CaptureAsync(ClipboardPayload.FromText(new string('x', 600))));
        Assert.Empty(history.Items);
        await history.CaptureAsync(ClipboardPayload.FromText(new string('a', 400)));
        await history.CaptureAsync(ClipboardPayload.FromText(new string('b', 400)));
        Assert.Equal(new string('b', 400), Assert.Single(history.Items).Payload.Text);
    }
    [Fact]
    public void PrivacyGateHandlesExcludeAndCanIncludeClipboardFormats()
    {
        Assert.True(ClipboardPrivacyRules.ShouldExclude([ClipboardPrivacyRules.ExcludeFormat], _ => 1));
        Assert.True(ClipboardPrivacyRules.ShouldExclude(["CanIncludeInClipboardHistory"], name => name == "CanIncludeInClipboardHistory" ? 0u : null));
        Assert.False(ClipboardPrivacyRules.ShouldExclude(["CanIncludeInClipboardHistory"], name => name == "CanIncludeInClipboardHistory" ? 1u : null));
        Assert.True(ClipboardPrivacyRules.ShouldExclude(["CanIncludeInClipboardHistory"], _ => null));
        Assert.False(ClipboardPrivacyRules.ShouldExclude(["UnicodeText"], _ => null));
    }

    [Fact]
    public async Task HistoryDeduplicatesAndBoundsRetainedRows()
    {
        using var folder = new TemporaryDirectory();
        var options = new ClipboardCaptureOptions { ExplicitlyEnabled = true, MaxItems = 2, MaxPinnedItems = 1 };
        await using var history = new ClipboardHistoryService(new EncryptedClipboardRepository(folder.PathFor("history.db"), new TestProtector()), options);
        await history.InitializeAsync();
        await history.CaptureAsync(ClipboardPayload.FromText("same-value"));
        await history.CaptureAsync(ClipboardPayload.FromText("same-value"));
        Assert.Single(history.Items);
        await history.CaptureAsync(ClipboardPayload.FromText("second"));
        await history.CaptureAsync(ClipboardPayload.FromText("third"));
        Assert.Equal(new[] { "third", "second" }, history.Items.Select(x => x.Payload.Text));
        Assert.Equal(2, history.Items.Count);
    }

    [Fact]
    public async Task PinnedRowsAreKeptBeforeUnpinnedRowsWhenTrimming()
    {
        using var folder = new TemporaryDirectory();
        var options = new ClipboardCaptureOptions { ExplicitlyEnabled = true, MaxItems = 2, MaxPinnedItems = 1 };
        await using var history = new ClipboardHistoryService(new EncryptedClipboardRepository(folder.PathFor("history.db"), new TestProtector()), options);
        await history.InitializeAsync();
        await history.CaptureAsync(ClipboardPayload.FromText("keep"));
        Assert.True(await history.PinAsync(history.Items[0].Id, true));
        await history.CaptureAsync(ClipboardPayload.FromText("old unpinned"));
        await history.CaptureAsync(ClipboardPayload.FromText("newest"));
        Assert.Contains(history.Items, x => x.Payload.Text == "keep" && x.IsPinned);
        Assert.Contains(history.Items, x => x.Payload.Text == "newest");
        Assert.DoesNotContain(history.Items, x => x.Payload.Text == "old unpinned");
    }

    [Fact]
    public async Task EncryptedDatabaseCanRestoreEntriesWithoutPlaintextOnDisk()
    {
        using var folder = new TemporaryDirectory();
        var database = folder.PathFor("history.db");
        const string secret = "sensitive clipboard text 903174";
        var protector = new TestProtector();
        await using (var history = new ClipboardHistoryService(new EncryptedClipboardRepository(database, protector), new() { ExplicitlyEnabled = true }))
        {
            await history.InitializeAsync();
            await history.CaptureAsync(ClipboardPayload.FromText(secret));
        }
        var disk = File.ReadAllBytes(database);
        Assert.False(ContainsBytes(disk, Encoding.UTF8.GetBytes(secret)));

        await using var reopened = new ClipboardHistoryService(new EncryptedClipboardRepository(database, protector), new() { ExplicitlyEnabled = true });
        await reopened.InitializeAsync();
        Assert.Equal(secret, Assert.Single(reopened.Items).Payload.Text);
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    [Fact]
    public async Task RestoreCallbackReceivesOriginalPayloadAndClearDoesNotAffectShelfFiles()
    {
        using var folder = new TemporaryDirectory();
        await using var history = new ClipboardHistoryService(new EncryptedClipboardRepository(folder.PathFor("history.db"), new TestProtector()), new() { ExplicitlyEnabled = true });
        await history.InitializeAsync(); await history.CaptureAsync(ClipboardPayload.FromText("restore me"));
        ClipboardPayload? restored = null;
        Assert.True(await history.RestoreAsync(history.Items[0].Id, (payload, _) => { restored = payload; return Task.CompletedTask; }));
        Assert.Equal("restore me", restored?.Text);

        var original = folder.PathFor("source.txt"); await File.WriteAllTextAsync(original, "keep this file");
        await using var shelf = new FileShelfService(folder.PathFor("shelf.json"), folder.PathFor("managed"));
        await shelf.LoadAsync(); await shelf.AddFilesAsync([original]);
        await shelf.RemoveAsync(Assert.Single(shelf.Items).Id);
        await history.ClearAsync();
        await shelf.AddFilesAsync([original]);
        shelf.MaxManagedImages = 0;
        await shelf.CleanupManagedImagesAsync();
        Assert.True(File.Exists(original));
        Assert.Single(shelf.Items);
    }

    private sealed class TestProtector : IDataProtector
    {
        private static readonly byte[] Key = SHA256.HashData("Cyclop test-only key"u8);
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            using var aes = Aes.Create(); aes.Key = Key; aes.GenerateIV();
            using var output = new MemoryStream(); output.Write(aes.IV);
            using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true)) crypto.Write(plaintext);
            return output.ToArray();
        }
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            using var aes = Aes.Create(); aes.Key = Key;
            using var input = new MemoryStream(ciphertext.ToArray()); var iv = new byte[16]; _ = input.Read(iv); aes.IV = iv;
            using var crypto = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read);
            using var output = new MemoryStream(); crypto.CopyTo(output); return output.ToArray();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cyclop-tests-" + Guid.NewGuid().ToString("N"));
        public string PathFor(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
