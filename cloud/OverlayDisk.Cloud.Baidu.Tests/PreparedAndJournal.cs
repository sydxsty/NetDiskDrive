using System.Buffers.Binary;
using System.Text;
using OverlayDisk.Cloud.Baidu;
using OverlayDisk.Cloud.Contracts;

internal static partial class Program
{
    private static async Task PreparedReceipt()
    {
        var folder = CacheFolder(); using var server = new FakeBaidu();
        byte[] data = [17, 29, 41];
        var descriptor = new ImmutableObjectDescriptor("/bucket/object", data.Length, Sha(data));
        try
        {
            await using (var client = Cached(server, folder))
            {
                await client.CreateDirectoryAsync("/bucket");
                ICloudPreparedUploadStore capability = client;
                server.Calls.Clear();
                Assert(await capability.TryGetConfirmedReceiptAsync(descriptor) is null && server.Calls.Count == 0,
                    "A local receipt miss performed HEAD/LIST/download");
                using var source = new CountedSource(data);
                using var prepared = await PreparedUpload.CreateAsync(descriptor, source);
                Assert(source.BytesRead == data.Length && prepared.Md5 == Md5(data), "Factory did not read/hash one complete input");
                var created = await capability.PutPreparedAsync(prepared);
                Assert(!created.ReusedExisting && source.BytesRead == data.Length && server.Calls.Count == 4,
                    "Prepared upload reread input or changed the first-upload call count");
                server.Calls.Clear();
                Assert((await capability.TryGetConfirmedReceiptAsync(descriptor))?.ReusedExisting == true,
                    "A confirmed local receipt was not returned before source access");
                Assert(server.Calls.Count == 0, "Warm confirmed receipt used the network");
                await Error(async () => _ = await capability.TryGetConfirmedReceiptAsync(descriptor with { Sha256 = Sha([99, 98, 97]) }), "ObjectConflict");
                Assert(server.Calls.Count == 0, "Receipt conflict used the network");
            }
            server.Calls.Clear();
            await using (var reopened = Cached(server, folder))
            {
                // No source stream: a resumed coordinator repairs its receipt without opening the disk.
                Assert(await reopened.TryGetConfirmedReceiptAsync(descriptor) is { ReusedExisting: true }, "Restart lost a committed receipt delta");
                Assert(server.Calls.Count == 1 && Calls(server, "account") == 1, "Restart receipt used more than account scope validation");
                await reopened.DeleteAsync(descriptor.Path);
                server.Calls.Clear();
                Assert(await reopened.TryGetConfirmedReceiptAsync(descriptor) is null && server.Calls.Count == 0, "Deleted object retained its receipt");
            }
            using var generalServer = new FakeBaidu(); await using var general = Client(generalServer);
            Assert(await general.TryGetConfirmedReceiptAsync(descriptor) is null && generalServer.Calls.Count == 0,
                "Default non-exclusive backend invented a local receipt proof");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task PreparedRejection()
    {
        using var server = new FakeBaidu(); await using var client = Client(server);
        var expected = new byte[PreparedUpload.MaximumLength]; new Random(913).NextBytes(expected);
        var descriptor = new ImmutableObjectDescriptor("/object", expected.Length, Sha(expected));
        async Task PrepareAndPut(byte[] actual)
        {
            using var prepared = await PreparedUpload.CreateAsync(descriptor, new CountedSource(actual));
            await client.PutPreparedAsync(prepared);
        }
        var damaged = expected.ToArray(); damaged[^1] ^= 1;
        await Error(() => PrepareAndPut(damaged), "InputHashMismatch");
        await Error(() => PrepareAndPut(expected[..^1]), "InputLengthMismatch");
        await Error(() => PrepareAndPut([.. expected, 0]), "InputLengthMismatch");
        try
        {
            using var _ = await PreparedUpload.CreateAsync(descriptor with { Length = expected.Length + 1 }, new MemoryStream(expected));
            throw new Exception("Oversized prepared object accepted");
        }
        catch (ArgumentOutOfRangeException) { }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try
        {
            using var _ = await PreparedUpload.CreateAsync(descriptor, new CountedSource(expected), cancelled.Token);
            throw new Exception("Cancelled factory returned a prepared object");
        }
        catch (OperationCanceledException) { }
        Assert(server.Calls.Count == 0, "Invalid prepared input reached any API");
    }

    private static async Task PreparedOwnership()
    {
        using var server = new FakeBaidu { FailFirstPart = true }; await using var client = Cached(server);
        await client.CreateDirectoryAsync("/bucket");
        var original = new byte[CloudObjectGeometry.DefaultSize]; new Random(441).NextBytes(original);
        var sourceBytes = original.ToArray(); using var source = new CountedSource(sourceBytes);
        using var prepared = await PreparedUpload.CreateAsync(new("/bucket/object", original.Length, Sha(original)), source);
        using (var view = prepared.OpenRead())
        {
            Assert(!view.CanWrite && view is MemoryStream ms && !ms.TryGetBuffer(out _), "Prepared buffer was writable/exposed");
            try { view.WriteByte(0); throw new Exception("Read-only view accepted a write"); } catch (NotSupportedException) { }
        }
        Array.Fill(sourceBytes, (byte)0);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Override = async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/precreate") return null;
            reached.TrySetResult(); await release.Task.WaitAsync(ct); return null;
        };
        var upload = client.PutPreparedAsync(prepared);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        prepared.Dispose(); // Provider retains stable bytes for the complete retry loop.
        try { using var _ = prepared.OpenRead(); throw new Exception("Disposed owner opened a new reader"); } catch (ObjectDisposedException) { }
        release.TrySetResult(); await upload;
        Assert(server.PartAttempts == 2 && server.Files["/bucket/object"].SequenceEqual(original), "Ownership/disposal/retry changed uploaded content");
        Assert(source.BytesRead == original.Length, "Retry reread the original input");
    }

    private static async Task PreparedRecovery()
    {
        byte[] data = [1, 9, 2, 8, 3, 7]; var descriptor = new ImmutableObjectDescriptor("/bucket/object", data.Length, Sha(data));
        foreach (var mode in new[] { "lost", "opaque", "missing" })
        {
            using var server = new FakeBaidu { LoseCommitReply = mode == "lost", OpaqueMd5 = mode == "opaque", MissingCreateFields = mode == "missing" };
            await using var client = Cached(server); await client.CreateDirectoryAsync("/bucket");
            using var source = new CountedSource(data);
            using var prepared = await PreparedUpload.CreateAsync(descriptor, source);
            _ = await client.PutPreparedAsync(prepared);
            Assert(server.Downloads == (mode == "lost" ? 1 : 0) && source.BytesRead == data.Length, "Successful acknowledgment downloaded again, or uncertain recovery skipped verification/reread input");
            server.Calls.Clear();
            Assert(await client.TryGetConfirmedReceiptAsync(descriptor) is not null && server.Calls.Count == 0, "Recovered prepared receipt was not reusable");
        }
        using var conflictServer = new FakeBaidu(); conflictServer.SeedDirectory("/bucket"); conflictServer.Files[descriptor.Path] = [9, 9, 9, 9, 9, 9];
        await using var conflict = Cached(conflictServer);
        using var item = await PreparedUpload.CreateAsync(descriptor, new MemoryStream(data, false));
        await Error(async () => _ = await conflict.PutPreparedAsync(item), "ObjectConflict");
        Assert(conflictServer.UploadedParts == 0 && conflictServer.Files[descriptor.Path][0] == 9, "Prepared conflict overwrote existing bytes");
    }

    private static string DirectoryCheckpoint(string folder, string path = "/bucket") => Directory.GetFiles(folder,
        Sha(Encoding.UTF8.GetBytes(path)) + ".cache", SearchOption.AllDirectories).Single();

    private static async Task JournalBatch()
    {
        var folder = CacheFolder(); using var server = new FakeBaidu();
        try
        {
            await using (var client = Cached(server, folder))
            {
                await client.CreateDirectoryAsync("/bucket");
                string checkpoint = DirectoryCheckpoint(folder); byte[] initial = await File.ReadAllBytesAsync(checkpoint);
                for (int i = 0; i < 96; i++) await Put(client, $"/bucket/item{i:D3}", [(byte)i]);
                Assert((await File.ReadAllBytesAsync(checkpoint)).SequenceEqual(initial), "A small delta batch rewrote the complete parent checkpoint");
                Assert(new FileInfo(Path.ChangeExtension(checkpoint, ".wal")).Length > 56, "Successful deltas were not journaled");
                for (int i = 96; i < 128; i++) await Put(client, $"/bucket/item{i:D3}", [(byte)i]);
                Assert(!(await File.ReadAllBytesAsync(checkpoint)).SequenceEqual(initial), "Bounded journal never checkpointed");
                Assert(new FileInfo(Path.ChangeExtension(checkpoint, ".wal")).Length == 56, "Checkpoint did not start a fresh journal generation");
                await client.DeleteManyAsync(["/bucket/item001", "/bucket/item064", "/bucket/item127"]);
            }
            server.Calls.Clear(); await using var reopened = Cached(server, folder, capacity: 1);
            foreach (var i in new[] { 0, 65, 126 })
                Assert(await reopened.TryGetConfirmedReceiptAsync(new($"/bucket/item{i:D3}", 1, Sha([(byte)i]))) is not null, "Checkpoint/replayed journal lost a retained receipt");
            foreach (var i in new[] { 1, 64, 127 })
                Assert(await reopened.HeadAsync($"/bucket/item{i:D3}") is null, "Delete delta was not replayed");
            Assert(server.Calls.Count == 1 && Calls(server, "list") == 0, "Checkpoint plus deltas required fresh listings after RAM eviction/restart");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task JournalDamage()
    {
        foreach (var damage in new[] { "torn", "checksum", "missing", "generation", "unfinished", "missing-header" })
        {
            var folder = CacheFolder(); using var server = new FakeBaidu();
            try
            {
                await using (var client = Cached(server, folder))
                {
                    await client.CreateDirectoryAsync("/bucket"); await Put(client, "/bucket/first", [1]); await Put(client, "/bucket/last", [2]);
                }
                var wal = Path.ChangeExtension(DirectoryCheckpoint(folder), ".wal"); var bytes = await File.ReadAllBytesAsync(wal);
                if (damage == "missing") File.Delete(wal);
                else
                {
                    if (damage == "torn") bytes = bytes[..^7];
                    if (damage == "checksum") bytes[^1] ^= 1;
                    if (damage == "generation") bytes[8] ^= 1;
                    if (damage == "missing-header") bytes = [];
                    if (damage == "unfinished")
                    {
                        int position = 56, last = position;
                        while (position < bytes.Length) { last = position; position += 4 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position, 4)) + 32; }
                        bytes = bytes[..last]; // Last BEGIN persisted, its COMMIT did not.
                    }
                    await File.WriteAllBytesAsync(wal, bytes);
                }
                server.Calls.Clear(); await using var reopened = Cached(server, folder);
                Assert(await reopened.TryGetConfirmedReceiptAsync(new("/bucket/last", 1, Sha([2]))) is null, "Damaged journal returned a trusted receipt: " + damage);
                Assert(Calls(server, "list") == 0, "Local receipt miss silently performed remote precheck");
                Assert(await reopened.HeadAsync("/bucket/last") is not null && Calls(server, "list") == 1,
                    "Damaged/unfinished journal retained a false absence proof: " + damage);
                Assert(await reopened.HeadAsync("/bucket/first") is not null && Calls(server, "list") == 1, "Repaired parent was repeatedly listed");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }

    private static async Task<int> JournalCommitCrashChild(string folder)
    {
        using var server = new FakeBaidu(); server.SeedDirectory("/bucket");
        await using var client = Cached(server, folder);
        await Put(client, "/bucket/committed", [4, 2]);
        Console.WriteLine("journal-receipt-durable"); Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 2;
    }

    private static async Task JournalCommitCrash()
    {
        var folder = CacheFolder(); using var server = new FakeBaidu();
        try
        {
            await using (var first = Cached(server, folder)) { await first.CreateDirectoryAsync("/bucket"); }
            string checkpoint = DirectoryCheckpoint(folder); var before = await File.ReadAllBytesAsync(checkpoint);
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--journal-commit-crash-helper"); start.ArgumentList.Add(folder);
            using var child = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start journal crash helper");
            try
            {
                Assert(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) == "journal-receipt-durable", "Child did not commit its journal receipt");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            Assert((await File.ReadAllBytesAsync(checkpoint)).SequenceEqual(before), "Child wrote a full checkpoint instead of the receipt journal");
            server.Files["/bucket/committed"] = [4, 2]; server.Calls.Clear();
            await using var reopened = Cached(server, folder);
            Assert(await reopened.TryGetConfirmedReceiptAsync(new("/bucket/committed", 2, Sha([4, 2]))) is { ReusedExisting: true }, "Process kill lost a durable journal receipt");
            Assert(server.Calls.Count == 1 && Calls(server, "account") == 1, "Committed journal receipt required remote content/metadata checks");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private sealed class CountedSource(byte[] bytes) : Stream
    {
        private int position;
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = Math.Min(Math.Min(buffer.Length, 997), bytes.Length - position);
            bytes.AsSpan(position, count).CopyTo(buffer); position += count; BytesRead += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
