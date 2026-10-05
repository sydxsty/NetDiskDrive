using OverlayDisk.Cloud.Contracts;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OverlayDisk.Cloud.Baidu;

internal sealed partial class BaiduMetadataCache
{
    private static readonly byte[] JournalMagic = "ODWAL01\n"u8.ToArray();
    private const int JournalHeaderLength = 8 + 16 + 32;
    private const int MaximumRecordBytes = 4 * 1024 * 1024;
    private const int CheckpointJournalBytes = 16 * 1024 * 1024;
    private const int MaximumJournalBytes = CheckpointJournalBytes + 2 * MaximumRecordBytes;
    private sealed record JournalRecord(long Sequence, Guid CheckpointId, string Kind, Guid MutationId,
        CachedCloudObject[]? Upserts, string[]? Removes);

    private string JournalPath(string path) => Path.ChangeExtension(FilePath(path), ".wal");

    private async Task AppendAsync(string path, DirectoryState state, string kind, Guid mutationId,
        IReadOnlyList<CachedCloudObject>? upserts, IReadOnlyList<string>? removes, CancellationToken ct)
    {
        if (directory is null || state.Entries is null) return;
        try
        {
            if (state.CheckpointId == Guid.Empty) throw new IOException("Missing directory checkpoint.");
            var payload = JsonSerializer.SerializeToUtf8Bytes(new JournalRecord(checked(state.JournalSequence + 1), state.CheckpointId,
                kind, mutationId, upserts?.ToArray(), removes?.ToArray()), Json);
            if (payload.Length > MaximumRecordBytes) throw new IOException("Directory delta exceeds its bound.");
            var frame = new byte[4 + payload.Length + 32];
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
            payload.CopyTo(frame, 4);
            SHA256.HashData(payload).CopyTo(frame, 4 + payload.Length);
            await using (var file = new FileStream(JournalPath(path), FileMode.Open, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (file.Length != state.JournalBytes) throw new IOException("Directory journal changed.");
                file.Position = file.Length;
                await file.WriteAsync(frame, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(true);
            }
            state.JournalSequence++;
            state.JournalRecords++;
            state.JournalBytes += frame.Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Even a cancelled/partial BEGIN must not leave an older complete
            // negative proof usable. No cloud mutation starts before this returns.
            state.Entries = null;
            InvalidateFile(path);
            if (error is OperationCanceledException) throw;
            throw Failure("MetadataCacheWriteFailed");
        }
    }

    private async Task MaybeCheckpointAsync(string path, DirectoryState state, CancellationToken ct)
    {
        if (state.Entries is null) return;
        if (state.Entries.Count > MaximumDirectoryEntries)
        {
            InvalidateFile(path); state.Entries = null; return;
        }
        if (state.Mutations != 0) return;
        // Small deltas append only. Growth adapts to live directory size, so a
        // large directory is not copied once per fixed small batch of objects.
        if (state.JournalRecords >= Math.Max(256, state.Entries.Count) || state.JournalBytes >= CheckpointJournalBytes)
            await SaveAsync(path, state, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, CachedCloudObject>?> LoadAsync(string path, DirectoryState state, CancellationToken ct)
    {
        var file = FilePath(path); if (!File.Exists(file)) return null;
        try
        {
            byte[] bytes;
            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous))
            {
                if (stream.Length < Magic.Length + 65 || stream.Length > MaximumFileBytes) return null;
                bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            }
            if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
            var offset = Magic.Length + 65;
            if (bytes[offset - 1] != (byte)'\n' || Encoding.ASCII.GetString(bytes, Magic.Length, 64) != Hash(bytes.AsSpan(offset))) return null;
            var payload = JsonSerializer.Deserialize<Payload>(bytes.AsSpan(offset), Json);
            if (payload is null || payload.Version != 2 || payload.ProviderId != providerId || payload.AccountId != accountId || payload.Directory != path ||
                payload.CheckpointId == Guid.Empty || payload.Entries is null || payload.Entries.Length > MaximumDirectoryEntries) return null;
            var entries = new Dictionary<string, CachedCloudObject>(StringComparer.Ordinal);
            foreach (var entry in payload.Entries)
                if (!ValidEntry(path, entry) || !entries.TryAdd(entry.Info.Path, entry)) return null;

            await using var journal = new FileStream(JournalPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            if (journal.Length < JournalHeaderLength || journal.Length > MaximumJournalBytes) return null;
            var header = new byte[JournalHeaderLength]; await journal.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            if (!header.AsSpan(0, 8).SequenceEqual(JournalMagic) || new Guid(header.AsSpan(8, 16)) != payload.CheckpointId ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(header.AsSpan(0, 24)), header.AsSpan(24))) return null;
            long sequence = 0;
            int records = 0;
            var pending = new HashSet<Guid>();
            var lengthBytes = new byte[4];
            while (journal.Position < journal.Length)
            {
                await journal.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (length <= 0 || length > MaximumRecordBytes || journal.Length - journal.Position < length + 32L) return null;
                var recordBytes = new byte[length]; var checksum = new byte[32];
                await journal.ReadExactlyAsync(recordBytes, ct).ConfigureAwait(false);
                await journal.ReadExactlyAsync(checksum, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(recordBytes), checksum)) return null;
                var record = JsonSerializer.Deserialize<JournalRecord>(recordBytes, Json);
                if (record is null || record.Sequence != ++sequence || record.CheckpointId != payload.CheckpointId) return null;
                records++;
                if (record.Kind == "begin")
                {
                    if (record.MutationId == Guid.Empty || !pending.Add(record.MutationId) || record.Upserts is not null || record.Removes is not null) return null;
                    continue;
                }
                if (record.Kind == "commit")
                {
                    if (!pending.Remove(record.MutationId)) return null;
                }
                else if (record.Kind != "patch" || record.MutationId != Guid.Empty) return null;
                if (record.Upserts is { } upserts)
                    foreach (var entry in upserts)
                    {
                        if (!ValidEntry(path, entry)) return null;
                        entries[entry.Info.Path] = entry;
                    }
                if (record.Removes is { } removes)
                    foreach (var removed in removes)
                    {
                        if (string.IsNullOrEmpty(removed) || Parent(removed) != path) return null;
                        entries.Remove(removed);
                    }
                if (entries.Count > MaximumDirectoryEntries) return null;
            }
            // A successful remote request whose COMMIT is absent remains unknown;
            // do not silently replay only the earlier complete prefix as current.
            if (pending.Count != 0) return null;
            state.CheckpointId = payload.CheckpointId;
            state.JournalSequence = sequence; state.JournalRecords = records; state.JournalBytes = journal.Length;
            return entries;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private static bool ValidEntry(string parent, CachedCloudObject? entry) => entry?.Info is { } info &&
        !string.IsNullOrEmpty(info.Path) && info.Path.Length <= 4096 && Parent(info.Path) == parent && info.Length >= 0 &&
        (entry.Sha256 is null || entry.Sha256.Length == 64 && entry.Sha256.All(Uri.IsHexDigit)) &&
        (entry.Canonical is not { } canonical || canonical.Path == info.Path && canonical.Codec == ObjectTransport.Codec &&
            CloudObjectGeometry.IsSupported(canonical.Length) && canonical.Sha256 is { Length: 64 } && canonical.Sha256.All(Uri.IsHexDigit) &&
            entry.Sha256 is not null && !info.IsDirectory && info.Length > ObjectTransport.HeaderLength && info.Length <= ObjectTransport.MaxWireLength(canonical.Length));

    private async Task SaveAsync(string path, DirectoryState state, CancellationToken ct)
    {
        if (directory is null || state.Entries is null) return;
        if (state.Mutations != 0) throw new InvalidOperationException("Cannot checkpoint an unfinished directory mutation.");
        if (state.Entries.Count > MaximumDirectoryEntries) { InvalidateFile(path); state.Entries = null; return; }
        var checkpointId = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(2, providerId, accountId, path, checkpointId, state.Entries.Values.ToArray()), Json);
        if (payload.Length + Magic.Length + 65 > MaximumFileBytes) { InvalidateFile(path); state.Entries = null; return; }
        var file = FilePath(path); var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // First invalidate the old checkpoint durably. Any crash during the
            // two-file generation switch then causes a fresh LIST, never stale
            // absence. The new checkpoint must match the synced journal header.
            InvalidateFile(path);
            var header = new byte[JournalHeaderLength];
            JournalMagic.CopyTo(header, 0); checkpointId.TryWriteBytes(header.AsSpan(8, 16));
            SHA256.HashData(header.AsSpan(0, 24)).CopyTo(header, 24);
            await using (var journal = new FileStream(JournalPath(path), FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await journal.WriteAsync(header, ct).ConfigureAwait(false);
                await journal.FlushAsync(ct).ConfigureAwait(false); journal.Flush(true);
            }
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(Magic, ct).ConfigureAwait(false);
                await output.WriteAsync(Encoding.ASCII.GetBytes(Hash(payload) + "\n"), ct).ConfigureAwait(false);
                await output.WriteAsync(payload, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(true);
            }
            File.Move(temporary, file, true);
            state.CheckpointId = checkpointId; state.JournalSequence = 0; state.JournalRecords = 0; state.JournalBytes = header.Length;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            state.Entries = null; InvalidateFile(path);
            if (error is OperationCanceledException) throw;
            throw Failure("MetadataCacheWriteFailed");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
