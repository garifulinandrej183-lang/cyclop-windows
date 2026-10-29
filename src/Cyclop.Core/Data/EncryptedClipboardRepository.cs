using Microsoft.Data.Sqlite;

namespace Cyclop.Core.Data;

/// <summary>Small SQLite envelope store. Only DPAPI ciphertext and non-sensitive metadata are persisted.</summary>
public sealed class EncryptedClipboardRepository : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EncryptedClipboardRepository(string databasePath, IDataProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA secure_delete=ON; CREATE TABLE IF NOT EXISTS clipboard_items (id TEXT PRIMARY KEY, created_utc INTEGER NOT NULL, pinned INTEGER NOT NULL, payload BLOB NOT NULL); CREATE INDEX IF NOT EXISTS ix_clipboard_created ON clipboard_items(created_utc DESC);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EncryptedClipboardRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, created_utc, pinned, payload, length(payload) FROM clipboard_items ORDER BY created_utc DESC";
            var records = new List<EncryptedClipboardRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            long totalBytes = 0;
            try
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var rowBytes = reader.GetInt64(4); totalBytes += rowBytes;
                    if (records.Count >= 500 || rowBytes > 32L * 1024 * 1024 || totalBytes > 128L * 1024 * 1024)
                        throw new InvalidDataException("The stored history exceeds its safe loading budget.");
                    records.Add(new(Guid.Parse(reader.GetString(0)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), reader.GetInt64(2) != 0, _protector.Unprotect((byte[])reader[3])));
                }
            }
            catch { foreach (var row in records) System.Security.Cryptography.CryptographicOperations.ZeroMemory(row.Plaintext); throw; }
            return records;
        }
        finally { _gate.Release(); }
    }

    public async Task UpsertAsync(Guid id, DateTimeOffset createdUtc, bool pinned, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken = default)
    {
        var ciphertext = _protector.Protect(plaintext.Span);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO clipboard_items(id,created_utc,pinned,payload) VALUES($id,$created,$pinned,$payload) ON CONFLICT(id) DO UPDATE SET created_utc=excluded.created_utc,pinned=excluded.pinned,payload=excluded.payload";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$created", createdUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            command.Parameters.Add("$payload", SqliteType.Blob).Value = ciphertext;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SetPinnedAsync(Guid id, bool pinned, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE clipboard_items SET pinned=$pinned WHERE id=$id";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => await ExecuteAsync("DELETE FROM clipboard_items WHERE id=$id", id, cancellationToken).ConfigureAwait(false);
    public async Task ClearAsync(CancellationToken cancellationToken = default) => await ExecuteAsync("DELETE FROM clipboard_items", null, cancellationToken).ConfigureAwait(false);
    public async Task DeleteManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in ids)
            {
                await using var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "DELETE FROM clipboard_items WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task ExecuteAsync(string sql, Guid? id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            if (id is not null) command.Parameters.AddWithValue("$id", id.Value.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
}

public sealed record EncryptedClipboardRecord(Guid Id, DateTimeOffset CreatedUtc, bool IsPinned, byte[] Plaintext);
