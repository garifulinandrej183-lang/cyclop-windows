using System.Text.Json;
using Cyclop.Core.Shelf;

namespace Cyclop.Tests;

public sealed class ShelfTests
{
    [Fact]
    public async Task ExternalManagedImageDeletionRemovesCardAndPersistedHashAllowsReimport()
    {
        using var folder = new TemporaryDirectory();
        var index = folder.PathFor("shelf.json");
        var managed = folder.PathFor("managed");
        var image = TestImage();
        await using var shelf = new FileShelfService(index, managed);
        await shelf.LoadAsync();
        var original = Assert.IsType<ShelfItem>(await shelf.AddImageAsync(image));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shelf.ItemsChanged += (_, _) => changed.TrySetResult();

        File.Delete(original.Path);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(shelf.Items);
        Assert.Empty(JsonSerializer.Deserialize<List<ShelfItem>>(await File.ReadAllBytesAsync(index))!);
        var reimported = Assert.IsType<ShelfItem>(await shelf.AddImageAsync(image));
        Assert.NotEqual(original.Path, reimported.Path);
        Assert.Single(shelf.Items);
    }

    [Fact]
    public async Task ImmediateReimportAfterExternalDeletionDoesNotReturnStaleDuplicate()
    {
        using var folder = new TemporaryDirectory();
        await using var shelf = new FileShelfService(folder.PathFor("shelf.json"), folder.PathFor("managed"));
        await shelf.LoadAsync();
        var image = TestImage();
        var original = Assert.IsType<ShelfItem>(await shelf.AddImageAsync(image));

        File.Delete(original.Path);
        var replacement = Assert.IsType<ShelfItem>(await shelf.AddImageAsync(image));

        Assert.NotEqual(original.Id, replacement.Id);
        Assert.NotEqual(original.Path, replacement.Path);
        Assert.Single(shelf.Items);
        Assert.Equal(replacement.Id, Assert.Single(JsonSerializer.Deserialize<List<ShelfItem>>(await File.ReadAllBytesAsync(folder.PathFor("shelf.json")))!).Id);
    }

    [Fact]
    public async Task RenamedManagedFolderPrunesRowsAndWatcherAttachesAfterFolderIsRecreated()
    {
        using var folder = new TemporaryDirectory();
        var managed = folder.PathFor("managed");
        await using var shelf = new FileShelfService(folder.PathFor("shelf.json"), managed);
        await shelf.LoadAsync();
        var first = Assert.IsType<ShelfItem>(await shelf.AddImageAsync(TestImage()));
        var firstRemoved = WaitForItemsAsync(shelf, () => shelf.Items.Count == 0);

        Directory.Move(managed, folder.PathFor("managed-renamed"));
        await firstRemoved;
        Directory.CreateDirectory(managed);
        var secondImage = TestImage().Append((byte)7).ToArray();
        var second = Assert.IsType<ShelfItem>(await shelf.AddImageAsync(secondImage));
        var secondRemoved = WaitForItemsAsync(shelf, () => shelf.Items.Count == 0);

        File.Delete(second.Path);
        await secondRemoved;

        Assert.False(File.Exists(first.Path));
        Assert.Empty(shelf.Items);
    }

    [Fact]
    public async Task LoadPrunesStaleManagedRowsAndKeepsUnavailableFileReferences()
    {
        using var folder = new TemporaryDirectory();
        var index = folder.PathFor("shelf.json");
        var managed = folder.PathFor("managed");
        string missingManagedPath;
        await using (var initial = new FileShelfService(index, managed))
        {
            await initial.LoadAsync();
            missingManagedPath = Assert.IsType<ShelfItem>(await initial.AddImageAsync(TestImage())).Path;
            await initial.AddFilesAsync([folder.PathFor("offline-drive\\source.png")]);
        }
        File.Delete(missingManagedPath);

        await using var reopened = new FileShelfService(index, managed);
        await reopened.LoadAsync();

        var row = Assert.Single(reopened.Items);
        Assert.Equal(ShelfItemKind.FileReference, row.Kind);
        Assert.False(row.IsAvailable);
        var persisted = JsonSerializer.Deserialize<List<ShelfItem>>(await File.ReadAllBytesAsync(index))!;
        Assert.Equal(row.Id, Assert.Single(persisted).Id);
    }

    [Fact]
    public async Task RapidManagedFolderReplacementKeepsWatchingNewScreenshots()
    {
        using var folder = new TemporaryDirectory();
        var managed = folder.PathFor("managed");
        await using var shelf = new FileShelfService(folder.PathFor("shelf.json"), managed);
        await shelf.LoadAsync();
        await shelf.AddImageAsync(TestImage());
        Directory.Move(managed, folder.PathFor("old-managed"));
        Directory.CreateDirectory(managed);
        await WaitForItemsAsync(shelf, () => shelf.Items.Count == 0);
        var replacement = Assert.IsType<ShelfItem>(await shelf.AddImageAsync(TestImage()));

        File.Delete(replacement.Path);
        await WaitForItemsAsync(shelf, () => shelf.Items.Count == 0);

        Assert.Empty(JsonSerializer.Deserialize<List<ShelfItem>>(await File.ReadAllBytesAsync(folder.PathFor("shelf.json")))!);
    }

    [Fact]
    public async Task ClearCardsPreservesManagedImageFiles()
    {
        using var folder = new TemporaryDirectory();
        var managed = folder.PathFor("managed");
        await using var shelf = new FileShelfService(folder.PathFor("shelf.json"), managed);
        await shelf.LoadAsync();
        var card = Assert.IsType<ShelfItem>(await shelf.AddImageAsync(TestImage()));

        await shelf.ClearCardsAsync();

        Assert.Empty(shelf.Items);
        Assert.True(File.Exists(card.Path));
        Assert.Empty(JsonSerializer.Deserialize<List<ShelfItem>>(await File.ReadAllBytesAsync(folder.PathFor("shelf.json")))!);
    }

    [Fact]
    public async Task PrunePublishesDeletionAndRetriesIndexAfterTemporaryWriteFailure()
    {
        using var folder = new TemporaryDirectory();
        var index = folder.PathFor("shelf.json");
        await using var shelf = new FileShelfService(index, folder.PathFor("managed"));
        await shelf.LoadAsync();
        var image = Assert.IsType<ShelfItem>(await shelf.AddImageAsync(TestImage()));
        // Block only the test's atomic-save target; no permissions or user data are changed.
        Directory.CreateDirectory(index + ".new");
        File.Delete(image.Path);
        var failure = await Record.ExceptionAsync(() => shelf.PruneMissingManagedImagesAsync());
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Empty(shelf.Items);

        Directory.Delete(index + ".new");
        await shelf.PruneMissingManagedImagesAsync();

        Assert.Empty(JsonSerializer.Deserialize<List<ShelfItem>>(await File.ReadAllBytesAsync(index))!);
    }

    private static byte[] TestImage() => [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4, 5, 6];

    private static async Task WaitForItemsAsync(FileShelfService shelf, Func<bool> condition)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) => { if (condition()) completion.TrySetResult(); };
        shelf.ItemsChanged += handler;
        try
        {
            if (condition()) completion.TrySetResult();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { shelf.ItemsChanged -= handler; }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cyclop-shelf-tests-" + Guid.NewGuid().ToString("N"));
        public string PathFor(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
