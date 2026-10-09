using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

internal static partial class Program
{
    private static readonly (string Name, Func<Task> Run)[] CloudEncryptionTests =
    [
        ("cloud-encryption: compress before encryption and restore 4/8/16 MiB with stable retry bytes", CloudEncryptionRoundTrip),
        ("cloud-encryption: salted keys password checking descriptor binding and tamper rejection", CloudEncryptionRejection),
        ("cloud-encryption: authenticated commits lazy reads stable salt and zero-request unchanged sync", CloudEncryptedPublication),
        ("cloud-encryption: lost object acknowledgement retries without changing immutable ciphertext", CloudEncryptedLostAcknowledgement),
        ("cloud-encryption: missing root created once for both listing routes and new publication", CloudMissingRoot)
    ];
    private static async Task<byte[]> EncryptedWire(byte[] raw, CanonicalObjectDescriptor descriptor, CloudEncryptionContext context)
    {
        using var upload = await PreparedObjectUpload.CreateAsync(descriptor, new MemoryStream(raw, false), context);
        using var source = upload.Wire.OpenRead(); using var bytes = new MemoryStream(); await source.CopyToAsync(bytes); return bytes.ToArray();
    }
    private static async Task CloudEncryptionRoundTrip()
    {
        string id = Guid.NewGuid().ToString(); using var context = CloudEncryptionContext.Create(id, "test password 中文");
        using var unlocked = CloudEncryptionContext.Unlock(context.Settings, "test password 中文");
        byte[] exported = context.ExportKey(); using var restored = CloudEncryptionContext.FromKey(context.Settings, exported); CryptographicOperations.ZeroMemory(exported);
        foreach (int size in new[] { 4 << 20, 8 << 20, 16 << 20 })
        {
            byte[] raw = new byte[size]; new Random(size).NextBytes(raw.AsSpan(0, 65536));
            var descriptor = new CanonicalObjectDescriptor(CloudRepository.ObjectPath(CloudRepository.RootPath(id), Guid.NewGuid().ToString()), size,
                CloudRepository.Hash(raw), ObjectTransport.EncryptedCodec, context.Settings.Id);
            byte[] wire = await EncryptedWire(raw, descriptor, context), retry = await EncryptedWire(raw, descriptor, unlocked);
            Assert(wire.Length < 100_000, "Encryption happened before compression, or the zero tail was retained");
            Assert(wire.SequenceEqual(retry), "Restart changed immutable encrypted object bytes");
            Assert(ObjectTransport.Decode(wire, descriptor, restored).SequenceEqual(raw), "Encrypted round-trip changed native bytes");
            byte[] otherPath = await EncryptedWire(raw, descriptor with { Path = descriptor.Path.Replace(".obj", "-other.obj") }, context);
            Assert(!wire.AsSpan(20, 12).SequenceEqual(otherPath.AsSpan(20, 12)), "Distinct objects reused nonce");
        }
    }
    private static async Task CloudEncryptionRejection()
    {
        string id = Guid.NewGuid().ToString(); using var context = CloudEncryptionContext.Create(id, "password");
        using var different = CloudEncryptionContext.Create(id, "password");
        Assert(context.Settings.Salt != different.Settings.Salt && context.Settings.Id != different.Settings.Id, "Disk salt was fixed globally");
        await Reject(() => { using var bad = CloudEncryptionContext.Unlock(context.Settings, "wrong"); return Task.CompletedTask; }, "Wrong password passed upfront validation");
        await Reject(() => { using var bad = CloudEncryptionContext.Unlock(context.Settings with { Iterations = int.MaxValue }, "password"); return Task.CompletedTask; }, "Unbounded attacker KDF parameters accepted");
        byte[] raw = new byte[4 << 20]; raw[8192] = 41;
        var descriptor = new CanonicalObjectDescriptor(CloudRepository.ObjectPath(CloudRepository.RootPath(id), Guid.NewGuid().ToString()), raw.Length,
            CloudRepository.Hash(raw), ObjectTransport.EncryptedCodec, context.Settings.Id);
        byte[] wire = await EncryptedWire(raw, descriptor, context);
        foreach (int offset in new[] { 0, 8, 12, 16, 20, 32, 64, 80, wire.Length - 1 })
        {
            byte[] bad = wire.ToArray(); bad[offset] ^= 1;
            await Reject(() => { ObjectTransport.Decode(bad, descriptor, context); return Task.CompletedTask; }, "Tampered encrypted object accepted");
        }
        foreach (var bad in new[] { descriptor with { Path = descriptor.Path.Replace(".obj", "-other.obj") }, descriptor with { Sha256 = new string('0', 64) } })
            await Reject(() => { ObjectTransport.Decode(wire, bad, context); return Task.CompletedTask; }, "Object substitution accepted");
        await Reject(() => { ObjectTransport.Decode(wire, descriptor); return Task.CompletedTask; }, "Encrypted object opened without a key");
        await Reject(() => { ObjectTransport.Decode(wire, descriptor, different); return Task.CompletedTask; }, "Different salt/key was accepted");
        byte[] compressed;
        using (var prepared = await PreparedObjectUpload.CreateAsync(descriptor with { Codec = ObjectTransport.Codec, EncryptionId = null }, new MemoryStream(raw)))
        { using var body = prepared.Wire.OpenRead(); using var sink = new MemoryStream(); await body.CopyToAsync(sink); compressed = sink.ToArray(); }
        await Reject(() => { ObjectTransport.Decode(compressed, descriptor, context); return Task.CompletedTask; }, "Plaintext downgrade accepted");
    }
    private static async Task CloudEncryptedPublication()
    {
        var f = new Fixture(); f.Volume.StartUnpublished(); using var context = CloudEncryptionContext.Create(f.Volume.Id, "cloud password");
        f.Repository.RegisterEncryption(f.Binding.RemoteRoot, context);
        var coordinator = new SyncCoordinator(f.Repository);
        var result = await coordinator.RunAsync(f.Volume, f.Binding, "Encrypted cloud", Fixture.Capacity, true, 2, null, default);
        Assert(result.Commit.Encrypted && result.Commit.Encryption == context.Settings && result.Commit.TransportCodec == ObjectTransport.EncryptedCodec, "Commit omitted cloud encryption settings");
        f.Repository.AuthenticateCommit(f.Binding.RemoteRoot, result.Commit);
        foreach (var commit in new[] { result.Commit with { Name = "tampered" }, result.Commit with { CapacityBytes = 128UL << 20 }, result.Commit with { RootSha256 = new string('0', 64) },
            result.Commit with { Encrypted = false, Encryption = null, Authentication = null, TransportCodec = ObjectTransport.Codec } })
            await Reject(() => { f.Repository.AuthenticateCommit(f.Binding.RemoteRoot, commit); return Task.CompletedTask; }, "Commit tamper/downgrade accepted");
        foreach (var pair in f.Volume.Objects)
        {
            byte[] read = await f.Repository.ReadObjectAsync(f.Binding.RemoteRoot, pair.Key, CloudRepository.Hash(pair.Value), pair.Value.Length, default);
            Assert(read.SequenceEqual(pair.Value), "Repository did not decrypt all data/index/root objects");
        }
        f.Events.Clear(); await coordinator.RunAsync(f.Volume, f.Binding, "Encrypted cloud", Fixture.Capacity, true, 2, null, default);
        Assert(!f.Events.Any(CloudCall), "Unchanged encrypted synchronization issued cloud requests");
        f.Volume.NextGeneration(); var next = await coordinator.RunAsync(f.Volume, f.Binding, "Encrypted cloud", Fixture.Capacity, true, 2, null, default);
        Assert(next.Commit.Encryption == result.Commit.Encryption, "A new generation rotated the disk salt");
        var locked = new CloudRepository(f.Store);
        var found = await locked.LatestAsync(f.Binding.RemoteRoot, default);
        Assert(found?.Commit == next.Commit, "Locked repository cannot discover public encryption settings");
        await Reject(() => { locked.AuthenticateCommit(f.Binding.RemoteRoot, next.Commit); return Task.CompletedTask; }, "Locked commit authenticated");
        using var opened = CloudEncryptionContext.Unlock(next.Commit.Encryption!, "cloud password"); locked.RegisterEncryption(f.Binding.RemoteRoot, opened);
        locked.AuthenticateCommit(f.Binding.RemoteRoot, next.Commit);
        var root = await locked.ReadObjectForReplicaAsync(f.Binding.RemoteRoot, next.Commit.RootObjectId, next.Commit.RootSha256, next.Commit.ObjectSizeBytes);
        Assert(root.Canonical.SequenceEqual(f.Volume.Objects[next.Commit.RootObjectId]), "Replica root did not decrypt");
    }
    private static async Task CloudEncryptedLostAcknowledgement()
    {
        var f = new Fixture(); f.Volume.StartUnpublished(); using var context = CloudEncryptionContext.Create(f.Volume.Id, "cloud password"); f.Repository.RegisterEncryption(f.Binding.RemoteRoot, context);
        bool failed = false; string? lostPath = null; byte[]? lostBytes = null;
        f.Store.AfterPut = path => { if (!failed && path.EndsWith(".obj", StringComparison.Ordinal)) { failed = true; lostPath = path; lostBytes = f.Store.Files[path].ToArray(); throw new IOException("Lost object acknowledgement"); } };
        var coordinator = new SyncCoordinator(f.Repository);
        await Reject(() => coordinator.RunAsync(f.Volume, f.Binding, "Encrypted cloud", Fixture.Capacity, true, 1, null, default), "Injected interruption ignored");
        f.Store.AfterPut = null; await coordinator.RunAsync(f.Volume, f.Binding, "Encrypted cloud", Fixture.Capacity, true, 1, null, default);
        Assert(failed && lostPath is not null && lostBytes!.SequenceEqual(f.Store.Files[lostPath]) && f.Volume.Committed, "Lost acknowledgement changed ciphertext or blocked publication");
    }
    private static async Task CloudMissingRoot()
    {
        foreach (bool baidu in new[] { false, true })
        {
            var f = new Fixture(); var missing = new MissingRootStore(f.Store, baidu); var repository = new CloudRepository(missing);
            Assert((await repository.ListDisksForReplicaAsync()).Count == 0 && missing.Created == 1, "Fresh empty cloud root was not created");
            Assert((await repository.ListDisksForReplicaAsync()).Count == 0 && missing.Created == 1, "Second listing recreated the root");
            f.Store.SeedDirectory(f.Binding.RemoteRoot); missing.MissingCommits = true;
            Assert((await repository.ListDisksForReplicaAsync()).Count == 0 && missing.Created == 1,
                "A partial cloud disk without commits aborted generic discovery or created extra folders");
        }
        var legacy = new Fixture(); await legacy.Repository.ListDisksAsync(default); await legacy.Repository.ListDisksAsync(default);
        Assert(legacy.Events.Count(e => e == "mkdir:/OverlayDisk") == 1, "Normal listing did not create a missing root exactly once");
        var fresh = new Fixture(); fresh.Volume.StartUnpublished(); await new SyncCoordinator(fresh.Repository).RunAsync(fresh.Volume, fresh.Binding, "New", Fixture.Capacity, false, 1, null, default);
        Assert(fresh.Events.Count(e => e == "mkdir:/OverlayDisk") == 1, "New synchronization did not create its root");
    }
    private sealed class MissingRootStore(MemoryStore inner, bool baidu) : ICloudObjectStore
    {
        public int Created; public bool MissingCommits; public string ProviderId => inner.ProviderId;
        public Task<CloudAccountInfo> ValidateAsync(CancellationToken ct = default) => inner.ValidateAsync(ct);
        public Task<CloudObjectInfo?> HeadAsync(string path, CancellationToken ct = default) => inner.HeadAsync(path, ct);
        public async IAsyncEnumerable<CloudObjectInfo> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (path == CloudRepository.BasePath && Created == 0 || MissingCommits && path.EndsWith("/commits", StringComparison.Ordinal))
            { if (baidu) throw new CloudProviderException("Baidu:-9", "Missing directory"); throw new CloudObjectNotFoundException(path); }
            await foreach (var item in inner.ListAsync(path, ct)) yield return item;
        }
        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) { Created++; return inner.CreateDirectoryAsync(path, ct); }
        public Task<CloudObjectInfo> PutImmutableAsync(string path, Stream content, long length, string sha256, CancellationToken ct = default) => inner.PutImmutableAsync(path, content, length, sha256, ct);
        public Task<Stream> OpenReadAsync(string path, CloudByteRange? range = null, CancellationToken ct = default) => inner.OpenReadAsync(path, range, ct);
        public Task DeleteAsync(string path, CancellationToken ct = default) => inner.DeleteAsync(path, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
