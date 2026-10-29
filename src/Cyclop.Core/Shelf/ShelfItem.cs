namespace Cyclop.Core.Shelf;

public enum ShelfItemKind { FileReference = 1, ManagedImage = 2 }
public sealed record ShelfItem(Guid Id, string Path, ShelfItemKind Kind, DateTimeOffset AddedUtc, string? Error = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsAvailable => Error is null && (File.Exists(Path) || Directory.Exists(Path));
}
