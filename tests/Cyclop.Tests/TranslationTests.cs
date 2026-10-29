using Cyclop.Core.Translation;

namespace Cyclop.Tests;

public sealed class TranslationTests
{
    [Fact]
    public void ManifestPinsRealArgosPackagesAndBothDirections()
    {
        Assert.Equal(new[] { "en-ru", "ru-en" }, ModelInstaller.Models.Select(model => model.Id));
        Assert.All(ModelInstaller.Models, model =>
        {
            Assert.Equal(64, model.Sha256.Length);
            Assert.Matches("^[0-9a-f]{64}$", model.Sha256);
            Assert.Equal("1.9", model.Version);
            Assert.StartsWith("https://argos-net.com/v1/", model.DownloadUri.AbsoluteUri);
            Assert.True(model.SizeBytes > 100_000_000);
        });
    }

    [Fact]
    public async Task InvalidDirectionDoesNotStartWorker()
    {
        using var client = new TranslationClient("missing-worker.exe", "models");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.TranslateAsync("hello", "fr-de"));
    }

    [Fact]
    public async Task MissingWorkerIsReportedOnlyWhenFirstRequestRuns()
    {
        using var client = new TranslationClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), "models");
        var secret = "TEXT_MUST_NOT_APPEAR_IN_DIAGNOSTICS";
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => client.TranslateAsync(secret, "en-ru"));
        Assert.DoesNotContain(secret, error.ToString());
    }

    [Fact]
    public async Task CancelledRequestDoesNotStartWorker()
    {
        using var client = new TranslationClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), "models");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.TranslateAsync("hello", "en-ru", cancellation.Token));
    }

    [Fact]
    public async Task OversizedRequestIsRejectedBeforeWorkerStart()
    {
        using var client = new TranslationClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), "models");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.TranslateAsync(new string('x', 2 * 1024 * 1024 + 1), "en-ru"));
    }

    [Fact]
    public async Task ImportRejectsAnUnpinnedArchiveBeforeExtraction()
    {
        var root = Path.Combine(Path.GetTempPath(), "cyclop-translation-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fake = Path.Combine(root, "fake.argosmodel");
        await File.WriteAllTextAsync(fake, "not a signed model");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ModelInstaller().ImportPackageAsync("en-ru", fake, Path.Combine(root, "models")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
