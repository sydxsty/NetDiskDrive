using System.Text.Json;
using OverlayDisk.Cloud.Baidu;
using OverlayDisk.Cloud.Contracts;

internal static partial class Program
{
    private static readonly (string Name, Func<Task> Run)[] ReplicaReaderBackendTests =
    [
        ("replica: descending discovery stops after its first page and uses global admission", ReplicaFirstPage),
        ("replica: partial listing stores positives without proving sibling absence", ReplicaPartialCache),
        ("replica: partial refresh retires persistent stale directory proofs", ReplicaPersistentCache),
        ("replica: fresh discovery bypasses cached missing directories", ReplicaFreshDirectory),
        ("replica: repeated malformed and unordered descending pages reject", ReplicaInvalidPages)
    ];
    private static BaiduClient ReplicaClient(FakeBaidu server, string? cache = null, bool exclusive = true, BaiduRequestScheduler? scheduler = null) =>
        new(Session, new BorrowedHandler(server), new BaiduClientOptions
        {
            PageSize = 2, MaximumListPages = 5, MaximumAttempts = 1,
            AssumeExclusiveWriter = exclusive, MetadataCacheDirectory = cache, MaximumCachedMetadataEntries = 64,
            RequestScheduler = scheduler ?? TestScheduler
        });
    private static async Task<List<CloudObjectInfo>> FirstReplicaPage(BaiduClient client, string directory)
    {
        var entries = new List<CloudObjectInfo>();
        await foreach (var item in client.ListByNameDescendingAsync(directory))
        { entries.Add(item); if (entries.Count == 2) break; }
        return entries;
    }
    private static async Task ReplicaFirstPage()
    {
        using var server = new FakeBaidu(); server.SeedDirectory("/commits");
        for (int i = 0; i < 1000; i++) server.Files["/commits/" + i.ToString("D20") + "-root.json"] = [1];
        var scheduler = new BaiduRequestScheduler(new(20, 1), new AdvancingClock());
        await using var client = ReplicaClient(server, exclusive: false, scheduler: scheduler);
        var page = await FirstReplicaPage(client, "/commits");
        Assert(page.Select(p => p.Path).SequenceEqual(new[] { "/commits/00000000000000000999-root.json", "/commits/00000000000000000998-root.json" }), "Newest files were not first");
        Assert(server.Calls.Count == 1 && server.Calls[0].Query.Contains("order=name") && server.Calls[0].Query.Contains("desc=1"), "Early stop fetched historical pages or extra API calls");
        Assert(client.GetRequestCounts().GetValueOrDefault("list-reader-latest") == 1 && scheduler.Snapshot().StartedRequests == 1 && scheduler.Snapshot().ActiveRequests == 0, "Reader page bypassed shared admission/counters or leaked the response lease");
    }
    private static async Task ReplicaPartialCache()
    {
        using var server = new FakeBaidu(); server.SeedDirectory("/bucket");
        foreach (var name in new[] { "a", "b", "c", "d" }) server.Files["/bucket/" + name] = [1];
        await using var client = ReplicaClient(server);
        var first = await FirstReplicaPage(client, "/bucket"); server.Calls.Clear();
        Assert((await client.HeadAsync("/bucket/d"))?.Path == first[0].Path && server.Calls.Count == 0, "Positive observation was not cached");
        Assert(await client.HeadAsync("/bucket/a") is not null && Calls(server, "list") == 3, "Unseen sibling was incorrectly treated as absent after a partial page");
        server.Files["/bucket/z"] = [2]; server.Calls.Clear();
        await FirstReplicaPage(client, "/bucket"); server.Calls.Clear();
        Assert(await client.HeadAsync("/bucket/z") is not null && server.Calls.Count == 0, "Fresh positive lost to an old negative directory proof");
        Assert(await client.HeadAsync("/bucket/unknown") is null && Calls(server, "list") == 3, "Partial refresh still claimed complete directory absence");
    }
    private static async Task ReplicaPersistentCache()
    {
        string cache = CacheFolder(); using var server = new FakeBaidu(); server.SeedDirectory("/bucket"); server.Files["/bucket/a"] = [1];
        try
        {
            await using (var client = ReplicaClient(server, cache))
            {
                Assert(await client.HeadAsync("/bucket/z") is null, "Fixture negative proof missing");
                server.Files["/bucket/z"] = [2];
                await FirstReplicaPage(client, "/bucket");
            }
            server.Files["/bucket/zz"] = [3]; server.Calls.Clear();
            await using var reopened = ReplicaClient(server, cache);
            Assert(await reopened.HeadAsync("/bucket/zz") is not null && Calls(server, "list") == 2,
                "Restart revived a stale complete listing after a partial reader refresh");
        }
        finally { Directory.Delete(cache, true); }
    }
    private static async Task ReplicaFreshDirectory()
    {
        using var server = new FakeBaidu(); await using var client = ReplicaClient(server);
        Assert(await client.HeadAsync("/commits") is null, "Fixture negative directory proof missing");
        server.SeedDirectory("/commits"); server.Files["/commits/latest.json"] = [1]; server.Calls.Clear();
        var page = await FirstReplicaPage(client, "/commits");
        Assert(page.Count == 1 && page[0].Path == "/commits/latest.json" && Calls(server, "list") == 1,
            "Reader consulted cached parent absence instead of fresh commit listing");
        await Error(async () => { await foreach (var _ in client.ListByNameDescendingAsync("/missing")) { } }, "ObjectNotFound");
    }
    private static async Task ReplicaInvalidPages()
    {
        using (var repeated = new FakeBaidu { RepeatPages = true })
        {
            repeated.SeedDirectory("/bucket"); repeated.Files["/bucket/a"] = [1]; repeated.Files["/bucket/b"] = [1];
            await using var client = ReplicaClient(repeated, exclusive: false);
            await Error(async () => { await foreach (var _ in client.ListByNameDescendingAsync("/bucket")) { } }, "RepeatedListing");
        }
        foreach (var (paths, error) in new[]
        {
            (new[] { "/bucket/a", "/bucket/b" }, "UnorderedListing"),
            (new[] { "/bucket/a", "/elsewhere/b" }, "MalformedListing"),
            (new[] { "/bucket/c", "/bucket/b", "/bucket/a" }, "MalformedListing")
        })
        {
            using var server = new FakeBaidu { Override = (request, _) => Task.FromResult<HttpResponseMessage?>(
                request.RequestUri!.AbsolutePath == "/rest/2.0/xpan/file" ? FakeBaidu.Raw(JsonSerializer.Serialize(new
                { errno = 0, list = paths.Select(path => new { path, size = 1, isdir = 0, fs_id = 4 }) })) : null) };
            await using var client = ReplicaClient(server, exclusive: false);
            await Error(async () => { await foreach (var _ in client.ListByNameDescendingAsync("/bucket")) { } }, error);
        }
    }
}
