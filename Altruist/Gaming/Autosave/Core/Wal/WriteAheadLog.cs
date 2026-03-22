/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Autosave;

/// <summary>
/// Write-ahead log for a specific entity type. Buffers dirty entity snapshots in memory
/// and periodically flushes to a dedicated .wal file. On server restart, the WAL is
/// replayed to recover unflushed data.
///
/// WAL files live in a dedicated directory (default: data/wal/), separate from app logs.
/// One file per entity type: PlayerVault.wal, ItemVault.wal, etc.
/// Files are self-rotating — truncated after every successful DB flush.
/// </summary>
public sealed class WriteAheadLog<T> : IDisposable where T : class, IVaultModel
{
    private readonly ConcurrentQueue<WalEntry> _buffer = new();
    private readonly string _walFilePath;
    private readonly Timer _flushTimer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public string FilePath => _walFilePath;
    public int BufferCount => _buffer.Count;

    public WriteAheadLog(string walDirectory, int flushIntervalSeconds, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger($"WAL<{typeof(T).Name}>");

        Directory.CreateDirectory(walDirectory);
        _walFilePath = Path.Combine(walDirectory, $"{typeof(T).Name}.wal");

        _flushTimer = new Timer(
            _ => _ = FlushBufferToDiskAsync(),
            null,
            TimeSpan.FromSeconds(flushIntervalSeconds),
            TimeSpan.FromSeconds(flushIntervalSeconds));
    }

    /// <summary>
    /// Append a dirty entity to the in-memory buffer. Zero disk I/O.
    /// </summary>
    public void Append(T entity, string ownerId)
    {
        var json = JsonSerializer.Serialize(entity, entity.GetType());
        _buffer.Enqueue(new WalEntry(typeof(T).AssemblyQualifiedName ?? typeof(T).FullName!, entity.StorageId, ownerId, json));
    }

    /// <summary>
    /// Flush the in-memory buffer to the WAL file on disk. One sequential async write.
    /// </summary>
    public async Task FlushBufferToDiskAsync()
    {
        if (_disposed) return;

        var entries = new List<WalEntry>();
        while (_buffer.TryDequeue(out var entry))
            entries.Add(entry);

        if (entries.Count == 0) return;

        await _writeLock.WaitAsync();
        try
        {
            var lines = entries.Select(e => JsonSerializer.Serialize(e));
            await File.AppendAllLinesAsync(_walFilePath, lines);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush WAL buffer to disk");
            // Re-queue entries so they're not lost
            foreach (var entry in entries)
                _buffer.Enqueue(entry);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Truncate the WAL file after a successful DB flush.
    /// Also drains any remaining buffer entries (they're already in the DB).
    /// </summary>
    public async Task TruncateAsync()
    {
        // Drain buffer (already flushed to DB)
        while (_buffer.TryDequeue(out _)) { }

        await _writeLock.WaitAsync();
        try
        {
            if (File.Exists(_walFilePath))
                File.Delete(_walFilePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Read all entries from the WAL file (for crash recovery on startup).
    /// Deduplicates by StorageId, keeping the latest entry per entity.
    /// </summary>
    public async Task<List<WalEntry>> RecoverAsync()
    {
        if (!File.Exists(_walFilePath))
            return new();

        await _writeLock.WaitAsync();
        try
        {
            var lines = await File.ReadAllLinesAsync(_walFilePath);
            var entries = new Dictionary<string, WalEntry>(); // storageId → latest entry

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var entry = JsonSerializer.Deserialize<WalEntry>(line);
                    if (entry != null)
                        entries[entry.StorageId] = entry; // Last write wins
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Skipping corrupt WAL entry");
                }
            }

            _logger.LogInformation("Recovered {Count} entities from WAL for {Type}", entries.Count, typeof(T).Name);
            return entries.Values.ToList();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flushTimer.Dispose();
        _writeLock.Dispose();
    }
}
