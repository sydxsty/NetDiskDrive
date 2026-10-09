using System.Net;
using System.Text.Json;
using OverlayDisk.Cloud.Baidu;
using OverlayDisk.Cloud.Contracts;
using OverlayDisk.Cloud.Sync;

internal static partial class Program
{
    private static readonly (string Name, Func<Task> Run)[] MissingDirectoryTests =
    [
        ("missing-directory: fresh root is created once with no failed-request diagnostic", MissingRootDiscovery),
        ("missing-directory: incomplete disks are skipped and a subsequent publication is discovered", MissingCommitDiscovery),
        ("missing-directory: deleted cached root is recreated after restart without stale child receipts", DeletedRootDiscovery),
        ("missing-directory: expected absence preserves unrelated cache and maps both listing routes", MissingListingSemantics),
        ("missing-directory: auth network and malformed replies remain visible failures and can retry", MissingDirectoryFailureVisibility)
    ];

    private static BaiduClient DirectoryClient(FakeBaidu server, List<BaiduDiagnostic> diagnostics, string? cache = null) =>
        new(Session, new BorrowedHandler(server), new BaiduClientOptions
        {
            RequestScheduler = TestScheduler, MaximumAttempts = 1, PageSize = 1000,
            AssumeExclusiveWriter = true, MetadataCacheDirectory = cache, Diagnostic = diagnostics.Add
        });
    private static int DirectoryCreates(FakeBaidu server) => server.Calls.Count(call => call.Path == "/api/create" && call.Body.Contains("isdir=1", StringComparison.Ordinal));
    private static void AssertNoFailures(List<BaiduDiagnostic> diagnostics) =>
        Assert(diagnostics.Count == 0, "Normal missing directory emitted an API failure: " + string.Join(',', diagnostics));

    private static async Task MissingRootDiscovery()
    {
        foreach (var missingCode in new[] { -9, 31066 })
        {
            using var server = new FakeBaidu(); var diagnostics = new List<BaiduDiagnostic>();
            if (missingCode == 31066)
                server.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath == "/rest/2.0/xpan/file" &&
                    Uri.UnescapeDataString(request.RequestUri.Query).Contains("dir=/OverlayDisk&", StringComparison.Ordinal) && DirectoryCreates(server) == 0
                    ? FakeBaidu.Raw("{\"errno\":31066}") : null);
            await using var client = DirectoryClient(server, diagnostics); var repository = new CloudRepository(client);
            Assert((await repository.ListDisksForReplicaAsync()).Count == 0 && DirectoryCreates(server) == 1,
                "Missing base folder was not created exactly once");
            AssertNoFailures(diagnostics); server.Override = null; server.Calls.Clear();
            Assert((await repository.ListDisksForReplicaAsync()).Count == 0 && DirectoryCreates(server) == 0 && Calls(server, "list") == 1,
                "Empty existing base folder made extra probes or duplicate mkdir requests");
            AssertNoFailures(diagnostics);
        }
    }

    private static RemoteCommit SeedDirectoryCommit(FakeBaidu server, string volumeId)
    {
        string root = CloudRepository.RootPath(volumeId);
        server.SeedDirectory(root); server.SeedDirectory(root + "/commits");
        var commit = new RemoteCommit(4, volumeId, "9d3c6fe0-f56f-4b1c-a5ad-cc0b8fe87a31", 1, "Disk", 64UL << 20,
            false, "00000000-0000-0000-0000-000000000001", new string('a', 64), DateTimeOffset.UnixEpoch);
        server.Files[CloudRepository.CommitPath(root, commit)] = JsonSerializer.SerializeToUtf8Bytes(commit, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return commit;
    }
    private static async Task MissingCommitDiscovery()
    {
        using var server = new FakeBaidu(); server.SeedDirectory(CloudRepository.BasePath);
        string incomplete = "cc75bd90-79b6-4909-8e3a-137ba16fb73b", good = "aa75bd90-79b6-4909-8e3a-137ba16fb73b";
        server.SeedDirectory(CloudRepository.RootPath(incomplete)); SeedDirectoryCommit(server, good);
        var diagnostics = new List<BaiduDiagnostic>(); await using var client = DirectoryClient(server, diagnostics);
        var repository = new CloudRepository(client);
        var first = await repository.ListDisksForReplicaAsync();
        Assert(first.Count == 1 && first[0].Id == good && DirectoryCreates(server) == 0,
            "An incomplete disk hid a valid disk or created unnecessary subdirectories");
        AssertNoFailures(diagnostics);
        SeedDirectoryCommit(server, incomplete); server.Calls.Clear();
        var retry = await repository.ListDisksForReplicaAsync();
        Assert(retry.Count == 2 && Calls(server, "list") == 3 && DirectoryCreates(server) == 0,
            "A stale missing-directory proof hid a newly published disk or added extra list requests");
        AssertNoFailures(diagnostics);
        // Missing immutable descriptor bytes are different from a not-yet-created
        // commits directory. A listed version must never silently disappear.
        server.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath == "/content"
            ? new HttpResponseMessage(HttpStatusCode.NotFound) : null);
        await Error(async () => _ = await repository.ListDisksForReplicaAsync(), "Http:404");
    }
    private static async Task DeletedRootDiscovery()
    {
        string cache = CacheFolder(); using var server = new FakeBaidu(); var diagnostics = new List<BaiduDiagnostic>();
        try
        {
            await using (var initial = DirectoryClient(server, diagnostics, cache))
            {
                await initial.CreateDirectoryAsync("/OverlayDisk/cached-child");
                Assert(await initial.HeadAsync("/OverlayDisk") is not null && await initial.HeadAsync("/OverlayDisk/cached-child") is not null,
                    "Fixture did not save positive directory proofs");
            }
            server.RemoveDirectory("/OverlayDisk"); server.Calls.Clear();
            await using var reopened = DirectoryClient(server, diagnostics, cache);
            Assert((await new CloudRepository(reopened).ListDisksForReplicaAsync()).Count == 0 && DirectoryCreates(server) == 1,
                "Stale positive proof prevented recreation of the deleted root");
            Assert(await reopened.HeadAsync("/OverlayDisk/cached-child") is null, "Deleted root left a live child receipt");
            await reopened.CreateDirectoryAsync("/OverlayDisk/cached-child");
            Assert(DirectoryCreates(server) == 2, "Deleted child was not recreated");
            AssertNoFailures(diagnostics);
        }
        finally { Directory.Delete(cache, true); }
    }
    private static async Task MissingListingSemantics()
    {
        using var server = new FakeBaidu(); server.SeedDirectory("/unrelated"); server.Files["/unrelated/keep"] = [7];
        var diagnostics = new List<BaiduDiagnostic>(); await using var client = DirectoryClient(server, diagnostics);
        Assert(await client.HeadAsync("/unrelated/keep") is not null, "Fixture was not cached");
        await Error(async () => { await foreach (var _ in client.ListAsync("/missing")) { } }, "ObjectNotFound");
        await Error(async () => { await foreach (var _ in client.ListByNameDescendingAsync("/missing")) { } }, "ObjectNotFound");
        Assert(await client.HeadAsync("/missing/child") is null, "HEAD of an absent parent did not return null");
        await client.DeleteAsync("/missing/child");
        server.Calls.Clear(); Assert(await client.HeadAsync("/unrelated/keep") is not null && server.Calls.Count == 0,
            "Expected absence discarded unrelated trusted metadata");
        AssertNoFailures(diagnostics);
    }
    private static async Task MissingDirectoryFailureVisibility()
    {
        foreach (var (reply, code) in new (Func<HttpResponseMessage> Reply, string Code)[]
        {
            (() => FakeBaidu.Raw("{\"errno\":-6}"), "Baidu:-6"),
            (() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), "Http:503"),
            (() => FakeBaidu.Raw("{\"errno\":0,\"list\":false}"), "MissingListing"),
            (() => throw new HttpRequestException("fixture network interruption"), "NetworkError")
        })
        {
            using var server = new FakeBaidu(); var diagnostics = new List<BaiduDiagnostic>();
            server.Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath == "/rest/2.0/xpan/file" ? reply() : null);
            await using var client = DirectoryClient(server, diagnostics); var repository = new CloudRepository(client);
            await Error(async () => _ = await repository.ListDisksForReplicaAsync(), code);
            Assert(DirectoryCreates(server) == 0, "A real failure was mistaken for absence and created a directory");
            if (code != "MissingListing") Assert(diagnostics.Any(item => item.Operation == "list-reader-latest" && item.Code == code),
                "Real request failure was hidden from diagnostics");
            server.Override = null; diagnostics.Clear();
            Assert((await repository.ListDisksForReplicaAsync()).Count == 0 && DirectoryCreates(server) == 1,
                "Retry after a real failure did not recover root discovery");
            AssertNoFailures(diagnostics);
        }
    }
}
