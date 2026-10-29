using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cyclop.Core.Translation;

public sealed record TranslationModel(
    string Id, string Direction, string Version, Uri DownloadUri, string Sha256,
    long SizeBytes, string Source);

public sealed record TranslationInstallProgress(string Stage, long BytesTransferred, long TotalBytes)
{
    public double? Percent => TotalBytes > 0 ? Math.Clamp(BytesTransferred * 100d / TotalBytes, 0, 100) : null;
    public override string ToString() => Percent is double percent
        ? $"{Stage}: {percent:0}% ({BytesTransferred:N0}/{TotalBytes:N0} bytes)"
        : Stage;
}

/// <summary>Explicit, hash-pinned download/import of the two supported Argos packages.</summary>
public sealed class ModelInstaller
{
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxEntries = 256;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static IReadOnlyList<TranslationModel> Models { get; } = Array.AsReadOnly(new[]
    {
        new TranslationModel("en-ru", "en-ru", "1.9",
            new Uri("https://argos-net.com/v1/translate-en_ru-1_9.argosmodel"),
            "591d743ae103752b88ffc38785c50421320f4eff93c8967e0d3d2e14d4e27811",
            195_746_693, "Argos Translate package index; argos-net.com (translate-en_ru-1_9.argosmodel)"),
        new TranslationModel("ru-en", "ru-en", "1.9",
            new Uri("https://argos-net.com/v1/translate-ru_en-1_9.argosmodel"),
            "e9ba8bf722d10a4a4c39f74289d5938fd47eac08dbe4ed0afd22d89445a5c3ac",
            156_239_112, "Argos Translate package index; argos-net.com (translate-ru_en-1_9.argosmodel)"),
    });

    public async Task DownloadAsync(string modelId, string modelsRoot,
        IProgress<TranslationInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var model = GetModel(modelId);
        Directory.CreateDirectory(Path.GetFullPath(modelsRoot));
        var archive = Path.Combine(Path.GetFullPath(modelsRoot), $".{model.Id}.{Guid.NewGuid():N}.download");
        try
        {
            using var response = await Http.GetAsync(model.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != model.SizeBytes)
                throw new InvalidDataException("The model server returned an unexpected archive size.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    copied += read;
                    if (copied > model.SizeBytes || copied > MaxArchiveBytes)
                        throw new InvalidDataException("The downloaded model exceeds the manifest size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new TranslationInstallProgress("download", copied, model.SizeBytes));
                }
                if (copied != model.SizeBytes) throw new InvalidDataException("The downloaded model size does not match the pinned manifest.");
            }
            await ImportPackageAsync(modelId, archive, modelsRoot, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { try { File.Delete(archive); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public async Task ImportPackageAsync(string modelId, string archivePath, string modelsRoot,
        IProgress<TranslationInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var model = GetModel(modelId);
        var archive = Path.GetFullPath(archivePath);
        var info = new FileInfo(archive);
        if (!info.Exists || info.Length != model.SizeBytes || info.Length > MaxArchiveBytes)
            throw new InvalidDataException("The Argos package size does not match the pinned model manifest.");
        var actualHash = await HashFileAsync(archive, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(model.Sha256)))
            throw new InvalidDataException("The Argos package SHA-256 does not match the pinned model manifest.");

        var root = Path.GetFullPath(modelsRoot);
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, $".{model.Id}.{Guid.NewGuid():N}.staging");
        var destination = Path.Combine(root, model.Direction);
        var backup = Path.Combine(root, $".{model.Id}.{Guid.NewGuid():N}.backup");
        Directory.CreateDirectory(staging);
        try
        {
            await ExtractValidatedPackageAsync(model, archive, staging, progress, cancellationToken).ConfigureAwait(false);
            var hadExisting = Directory.Exists(destination);
            if (hadExisting) await MoveDirectoryAsync(destination, backup, cancellationToken).ConfigureAwait(false);
            try { await MoveDirectoryAsync(staging, destination, cancellationToken).ConfigureAwait(false); }
            catch
            {
                if (hadExisting && Directory.Exists(backup)) await MoveDirectoryAsync(backup, destination, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            if (Directory.Exists(backup))
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (IOException) { /* Keep the recoverable backup if Windows still has a scanner handle. */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(backup) && !Directory.Exists(destination)) await MoveDirectoryAsync(backup, destination, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task ExtractValidatedPackageAsync(TranslationModel model, string archive, string staging,
        IProgress<TranslationInstallProgress>? progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count is 0 or > MaxEntries) throw new InvalidDataException("The Argos archive has an unexpected number of entries.");
        long expanded = 0;
        string? packageRoot = null;
        ZipArchiveEntry? metadataEntry = null;
        var files = new List<(ZipArchiveEntry Entry, string Relative)>();
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') || Path.IsPathRooted(entry.FullName))
                throw new InvalidDataException("The Argos archive contains an unsafe path.");
            var parts = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Contains(':')))
                throw new InvalidDataException("The Argos archive contains an unsafe path.");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000) throw new InvalidDataException("Symbolic links are not permitted in model packages.");
            expanded = checked(expanded + entry.Length);
            if (entry.Length < 0 || expanded > MaxExpandedBytes) throw new InvalidDataException("The Argos archive exceeds the expanded size limit.");
            if (packageRoot is null) packageRoot = parts[0];
            if (!string.Equals(parts[0], packageRoot, StringComparison.Ordinal)) throw new InvalidDataException("The Argos archive must contain one package root.");
            if (entry.FullName.EndsWith('/')) continue;
            if (parts.Length < 2) throw new InvalidDataException("The Argos archive has an unexpected layout.");
            var relative = string.Join('/', parts.Skip(1));
            if (relative == "metadata.json") metadataEntry = entry;
            if (relative == "sentencepiece.model" || relative == "metadata.json" || relative == "README.md" || relative.StartsWith("model/", StringComparison.Ordinal))
            {
                files.Add((entry, relative));
            }
            // Argos packages may contain Stanza .pt models. They are intentionally
            // ignored; this worker uses no Stanza/PyTorch code or model data.
        }

        if (metadataEntry is null) throw new InvalidDataException("The Argos package has no metadata.json.");
        await using (var stream = metadataEntry.Open())
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var json = document.RootElement;
            var expectedFrom = model.Direction[..2];
            var expectedTo = model.Direction[3..];
            if (!json.TryGetProperty("package_version", out var version) || version.GetString() != model.Version ||
                !json.TryGetProperty("from_code", out var from) || from.GetString() != expectedFrom ||
                !json.TryGetProperty("to_code", out var to) || to.GetString() != expectedTo)
                throw new InvalidDataException("The Argos package metadata does not match the pinned language pair/version.");
        }
        if (!files.Any(f => f.Relative == "model/model.bin") || !files.Any(f => f.Relative == "model/config.json") ||
            !files.Any(f => f.Relative == "sentencepiece.model"))
            throw new InvalidDataException("The Argos package is missing its CTranslate2 model or SentencePiece tokenizer.");

        var extracted = 0L;
        var payloadBytes = files.Sum(file => file.Entry.Length);
        foreach (var (entry, relative) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The Argos archive path escapes its staging directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                extracted += read;
                progress?.Report(new TranslationInstallProgress("extract", extracted, payloadBytes));
            }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task MoveDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        // File scanners can briefly retain handles after large native-model files
        // close. Keep the atomic directory swap, with bounded asynchronous retries.
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { Directory.Move(source, destination); return; }
            catch (Exception error) when (attempt < 5 && error is IOException or UnauthorizedAccessException)
            { await Task.Delay(TimeSpan.FromMilliseconds(80 * (1 << attempt)), cancellationToken).ConfigureAwait(false); }
        }
    }

    private static TranslationModel GetModel(string modelId) => Models.FirstOrDefault(model => model.Id == modelId)
        ?? throw new ArgumentOutOfRangeException(nameof(modelId), modelId, "Unknown offline translation model.");
}
