using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OverlayDisk.Cloud.Baidu;
using OverlayDisk.Cloud.Contracts;

internal static partial class Program
{
    private static readonly BaiduCookieSession Session = new([
        new("BDUSS", "fake-test-session-only", ".baidu.com"),
        new("STOKEN", "pan-value", "pan.baidu.com"),
        new("STOKEN", "passport-value", "passport.baidu.com"),
        new("LOCAL", "limited-path", "pan.baidu.com", "/disk")]);
    private static BaiduClientOptions Options(int pageSize = 1000) => new()
    {
        RequestScheduler = TestScheduler,
        PageSize = pageSize, MaximumAttempts = 2, MaximumTaskPolls = 3,
        MaximumListPages = 5, RetryDelay = TimeSpan.Zero, TaskPollDelay = TimeSpan.Zero
    };
    private static BaiduClient Client(FakeBaidu server, int pageSize = 1000) => new(Session, server, Options(pageSize));
    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static string Md5(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Error(Func<Task> work, string expected)
    {
        try { await work(); }
        catch (CloudProviderException e) { Assert(e.Code == expected, $"Expected {expected}, received {e.Code}"); return; }
        throw new Exception("Expected error " + expected);
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--cache-crash-helper") return await CacheCrashChild(args[1]);
        if (args.Length == 2 && args[0] == "--journal-commit-crash-helper") return await JournalCommitCrashChild(args[1]);
        if (args.Length == 2 && args[0] == "--journal-group-crash-helper") return await JournalGroupCrashChild(args[1]);
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("account and web session cookie scoping", Auth),
            ("strict JSON success and authentication errors", StrictSuccess),
            ("provider errors expose only operation and code", SafeOperationErrors),
            ("pagination and exact-path head", Pagination),
            ("repeated pages never become not-found", RepeatedPages),
            ("multipart immutable upload checksum fast path", Upload),
            ("existing immutable content and conflict", Existing),
            ("input SHA mismatch before cloud writes", InputMismatch),
            ("lost final reply recovers same path", LostCommit),
            ("upload transient retry and bad part MD5", PartRetry),
            ("HTML MIME accepts confirmed upload JSON but rejects HTML and unconfirmed parts", HtmlMimeUpload),
            ("opaque metadata checksum uses the successful create acknowledgment", ChecksumFallback),
            ("exact Range and ignored Range rejection", Range),
            ("short and overlong download streams", StreamLengths),
            ("delete waits for task and confirmed absence", Delete),
            ("delete failure and phantom success reject", DeleteFailure),
            ("redirect allowlist blocks credential forwarding", Redirect),
            ("legacy HTTP locate links are upgraded without cleartext requests", HttpsUpgrade),
            ("Windows DPAPI round-trip and tamper rejection", Vault)
            ,("exclusive writer cold warm and small increment request counts", CacheRequestCounts)
            ,("persistent metadata survives restart and RAM eviction", CacheRestart)
            ,("corrupt and wrong-account metadata force fresh listing", CacheCorruptionAndScope)
            ,("uncertain upload invalidates persistent absence proofs", CacheUploadFailures)
            ,("batch deletion confirms fresh absence per parent", BatchDelete)
            ,("account and upload endpoint TTL invalidation", CacheTtl)
            ,("process crash cannot retain a stale complete directory proof", CacheCrash)
            ,("parallel mutations publish a complete cache only after all outcomes", CacheParallel)
            ,("directory creation cannot erase concurrent child mutation knowledge", DirectoryCreationRace)
            ,("prepared receipt lookup precedes payload access and is local", PreparedReceipt)
            ,("prepared factory rejects tamper truncation and overlong input", PreparedRejection)
            ,("prepared owned buffer survives source mutation dispose and retry", PreparedOwnership)
            ,("prepared uncertainty keeps SHA readback and never accepts conflicts", PreparedRecovery)
            ,("metadata deltas append without rewriting each directory checkpoint", JournalBatch)
            ,("torn corrupt missing and unfinished journals invalidate completeness", JournalDamage)
            ,("durable receipt journal survives actual process kill before checkpoint", JournalCommitCrash)
            ,("PCS upload explicitly carries only the pan service credential pair", PcsCredentialPair)
            ,("unknown HTTP 403 is not retried and known authentication codes stay redacted", ForbiddenClassification)
            ,("global request rate is FIFO and configuration affects existing clients", GlobalRate)
            ,("global concurrency spans clients and complete download bodies", GlobalBodies)
            ,("queued cancellation and disposal release no phantom request slots", GlobalCancellation)
            ,("redirects retries and uploads use the same global admissions", GlobalAttemptCoverage)
            ,("known immutable reads use only locate and transfer without parent listing", KnownReadCounts)
            ,("invalid known descriptors reject before any HTTP request", KnownReadValidation)
            ,("known reads reject short extra and mismatched response bodies", KnownReadLengths)
            ,("fixed-length-multipart: prepared body is length-delimited and stable on retry", FixedLengthMultipartRetry)
            ,("upload-ack: successful opaque and incomplete replies never reread payload", UploadAckSuccess)
            ,("upload-ack: rapid completion is acknowledged without download", UploadAckRapid)
            ,("upload-ack: contradictory malformed or failed acknowledgments reject", UploadAckMalformed)
            ,("object-size: 4/8/16 MiB immutable multipart boundaries and isolated part retries", ObjectSizeMultipart)
            ,("object-size: invalid part indices checksums and declared sizes reject", ObjectSizeMultipartRejects)
            ,("object-size: 8/16 MiB known downloads preserve exact length without metadata probes", ObjectSizeKnownReads)
        };
        tests = tests.Concat(ObjectTransportTests).Concat(EncodedObjectTests).Concat(ReplicaReaderBackendTests).Concat(JournalGroupTests).ToArray();
        if (args.Length != 0)
        {
            if (args.Length != 2 || args[0] != "--filter") { Console.Error.WriteLine("Use --filter <test-name> to select a focused test."); return 2; }
            tests = tests.Where(test => args[1] == "upload-ack" ? UploadAckTests.Contains(test.Run.Method.Name) :
                test.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase) || test.Run.Method.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase)).ToArray();
            if (tests.Length == 0) { Console.Error.WriteLine("No test matched the filter; no tests were run."); return 2; }
        }
        foreach (var (name, run) in tests)
        {
            try { await run(); Console.WriteLine("PASS " + name); }
            catch (Exception e) { Console.Error.WriteLine("FAIL " + name + ": " + e.GetType().Name + " " + e.Message); return 1; }
        }
        Console.WriteLine($"{tests.Length} backend functional tests passed; no real account/network used.");
        return 0;
    }

    private static async Task Auth()
    {
        using var server = new FakeBaidu();
        await using var client = Client(server);
        var account = await client.ValidateAsync();
        Assert(account.AccountId == "1234" && account.DisplayName == "Test User", "Account fields differ");
        await client.InitializeWebSessionAsync();
        await client.InitializeWebSessionAsync();
        Assert(server.Calls.Count(x => x.Path == "/disk/home") == 1, "Warmup was not cached");
        Assert(server.Calls.Where(x => x.Host == "pan.baidu.com").All(x => !x.Cookie.Contains("passport-value")), "Passport cookie escaped its domain");
        Assert(server.Calls.Where(x => x.Path.StartsWith("/api/")).All(x => !x.Cookie.Contains("limited-path")), "Cookie path was ignored");
        Assert(client.ExportSession().Cookies.Any(c => c.Name == "pcsett"), "Server cookies not retained");
        var crowded = new BaiduCookieSession(Session.Cookies.Concat(Enumerable.Range(0, 40).Select(i => new BaiduCookieRecord("extra" + i, "test", ".baidu.com"))).ToArray());
        await using var crowdedClient = new BaiduClient(crowded, new FakeBaidu(), Options());
        _ = await crowdedClient.ValidateAsync();
        Assert(crowdedClient.ExportSession().Cookies.Count == crowded.Cookies.Count, "Cookie import silently evicted credentials");
        Assert(!Session.ToString().Contains("fake-test-session-only") && !Session.Cookies[0].ToString().Contains("fake-test-session-only"), "Cookie ToString leaks a value");
    }

    private static async Task StrictSuccess()
    {
        foreach (var (body, code, html) in new[]
        {
            ("{}", "MissingStatus", false), ("[]", "MalformedResponse", false),
            ("{\"errno\":0,\"data\":{\"errno\":-6}}", "Baidu:-6", false),
            ("{\"errno\":0,\"data\":{\"error_code\":31045}}", "Baidu:31045", false),
            ("<html>login</html>", "UnexpectedHtml", true)
        })
        {
            using var server = new FakeBaidu { Override = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeBaidu.Raw(body, html ? "text/html" : "application/json")) };
            await using var client = Client(server);
            await Error(async () => _ = await client.ValidateAsync(), code);
        }
    }

    private static async Task Pagination()
    {
        using var server = new FakeBaidu();
        foreach (var name in new[] { "a", "ab", "b", "z", "zz" }) server.Files["/" + name] = Encoding.UTF8.GetBytes(name);
        await using var client = Client(server, 2);
        var found = new List<CloudObjectInfo>();
        await foreach (var entry in client.ListAsync("/")) found.Add(entry);
        Assert(found.Count == 5, "Pagination lost entries");
        Assert((await client.HeadAsync("/z"))?.Path == "/z", "Head did not reach third page");
        Assert(await client.HeadAsync("/abc") is null, "Head used a prefix instead of exact path");
    }

    private static async Task SafeOperationErrors()
    {
        using var server = new FakeBaidu { Override = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeBaidu.Raw("{\"errno\":1,\"errmsg\":\"do-not-log-provider-body\"}")) };
        var diagnostics = new List<BaiduDiagnostic>();
        await using var client = new BaiduClient(Session, server, new BaiduClientOptions { Diagnostic = diagnostics.Add, RequestScheduler = TestScheduler });
        try { _ = await client.ValidateAsync(); throw new Exception("Provider error accepted"); }
        catch (CloudProviderException error)
        {
            Assert(error.Code == "Baidu:1" && error.Message.Contains("account") && !error.IsTransient, "Operation/code or retry classification changed");
            Assert(!error.Message.Contains("do-not-log-provider-body") && !error.Message.Contains("fake-test-session-only"), "Sensitive provider detail escaped");
        }
        Assert(diagnostics.Any(d => d.Operation == "account" && d.Code == "Baidu:1"), "API error diagnostic omitted its operation");
        Assert(server.Calls.Count == 1, "Code 1 was blindly retried");
    }

    private static async Task RepeatedPages()
    {
        using var server = new FakeBaidu { RepeatPages = true };
        server.Files["/a"] = [1]; server.Files["/b"] = [2];
        await using var client = Client(server, 2);
        await Error(async () => _ = await client.HeadAsync("/missing"), "RepeatedListing");
    }

    private static async Task Upload()
    {
        using var server = new FakeBaidu();
        var data = new byte[4 * 1024 * 1024 + 512];
        new Random(17).NextBytes(data);
        await using var client = Client(server);
        await client.CreateDirectoryAsync("/overlay/objects");
        using var input = new MemoryStream(data, false);
        var item = await client.PutImmutableAsync("/overlay/objects/id", input, data.Length, Sha(data));
        Assert(!item.ReusedExisting, "Newly created upload was reported as reuse");
        Assert(item.Length == data.Length && server.Files[item.Path].SequenceEqual(data), "Uploaded bytes differ");
        Assert(server.UploadedParts == 2 && server.Downloads == 0, "Successful upload should use its acknowledgment without readback");
        Assert(input.Position == data.Length, "Input ownership/position changed");
        Assert(server.Calls.Where(x => x.Path is "/api/precreate" or "/api/create").All(x => !x.Body.Contains("rtype=3")), "Overwrite was used");
    }

    private static async Task Existing()
    {
        using var server = new FakeBaidu(); var data = Encoding.UTF8.GetBytes("immutable object"); server.Files["/object"] = data;
        await using var client = Client(server);
        using var same = new MemoryStream(data);
        var existing = await client.PutImmutableAsync("/object", same, data.Length, Sha(data));
        Assert(existing.ReusedExisting, "Existing remote object was reported as a new upload");
        Assert(server.Downloads == 1 && server.UploadedParts == 0, "Existing content was not SHA-verified");
        var different = data.ToArray(); different[0] ^= 1;
        await Error(async () => _ = await client.PutImmutableAsync("/object", new MemoryStream(different), different.Length, Sha(different)), "ObjectConflict");
        Assert(server.Files["/object"].SequenceEqual(data), "Conflict overwrote existing bytes");
    }

    private static async Task InputMismatch()
    {
        using var server = new FakeBaidu(); await using var client = Client(server);
        await Error(async () => _ = await client.PutImmutableAsync("/object", new MemoryStream([1, 2]), 2, Sha([3, 4])), "InputHashMismatch");
        Assert(server.Calls.Count == 0, "Invalid input reached the network");
    }

    private static async Task LostCommit()
    {
        using var server = new FakeBaidu { LoseCommitReply = true }; await using var client = Client(server);
        var data = Encoding.UTF8.GetBytes("final response can be lost");
        var item = await client.PutImmutableAsync("/same-name", new MemoryStream(data), data.Length, Sha(data));
        Assert(item.ReusedExisting, "Recovered commit was reported as a newly confirmed transfer");
        Assert(item.Path == "/same-name" && server.Files.Count == 1 && server.Downloads == 1, "Lost commit was not recovered by SHA");
    }

    private static async Task PartRetry()
    {
        var data = Encoding.UTF8.GetBytes("part retry");
        using (var server = new FakeBaidu { FailFirstPart = true })
        {
            await using var client = Client(server);
            await client.PutImmutableAsync("/object", new MemoryStream(data), data.Length, Sha(data));
            Assert(server.PartAttempts == 2 && server.Files.Count == 1, "Transient part retry failed");
        }
        using (var server = new FakeBaidu { BadPartMd5 = true })
        {
            await using var client = Client(server);
            await Error(async () => _ = await client.PutImmutableAsync("/object", new MemoryStream(data), data.Length, Sha(data)), "UploadChecksumMismatch");
            Assert(server.Files.Count == 0, "Bad MD5 was committed");
        }
    }

    private static async Task ChecksumFallback()
    {
        using var server = new FakeBaidu { OpaqueMd5 = true }; await using var client = Client(server);
        var data = Encoding.UTF8.GetBytes("opaque provider checksum");
        await client.PutImmutableAsync("/object", new MemoryStream(data), data.Length, Sha(data));
        Assert(server.Downloads == 0, "Successful create with an opaque MD5 downloaded its payload again");
    }

    private static async Task HtmlMimeUpload()
    {
        var data = Encoding.UTF8.GetBytes("PCS JSON with legacy MIME type");
        using (var server = new FakeBaidu { HtmlPartJson = true })
        {
            await using var client = Client(server);
            await client.PutImmutableAsync("/object", new MemoryStream(data), data.Length, Sha(data));
            Assert(server.Files["/object"].SequenceEqual(data), "Valid JSON under text/html was not committed");
        }
        foreach (var (body, type, code) in new[]
        {
            ("<html><form>Sign in</form></html>", "text/html", "UnexpectedHtml"),
            ("<!doctype html><html>login</html>", "application/json", "UnexpectedHtml"),
            ("{}", "text/html", "MissingStatus"),
            ("{\"errno\":-6,\"md5\":\"" + Md5(data) + "\"}", "text/html", "Baidu:-6")
        })
        {
            using var server = new FakeBaidu
            {
                Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath == "/rest/2.0/pcs/superfile2" ? FakeBaidu.Raw(body, type) : null)
            };
            await using var client = Client(server);
            try
            {
                await client.PutImmutableAsync("/object", new MemoryStream(data), data.Length, Sha(data));
                throw new Exception("Invalid upload response was accepted");
            }
            catch (CloudProviderException error)
            {
                Assert(error.Code == code, "Unexpected upload response classification");
                if (code == "UnexpectedHtml") Assert(error.AuthenticationRequired, "HTML login page did not request reauthentication");
            }
            Assert(server.Files.Count == 0, "An invalid upload response reached create/commit");
        }
    }

    private static async Task Range()
    {
        using var server = new FakeBaidu(); var data = Enumerable.Range(0, 100).Select(x => (byte)x).ToArray(); server.Files["/object"] = data;
        await using var client = Client(server);
        await using (var stream = await client.OpenReadAsync("/object", new CloudByteRange(17, 19)))
        {
            using var copy = new MemoryStream(); await stream.CopyToAsync(copy);
            Assert(copy.ToArray().SequenceEqual(data[17..36]), "Range bytes differ");
        }
        server.IgnoreRange = true;
        await Error(async () => { await using var ignored = await client.OpenReadAsync("/object", new CloudByteRange(0, 10)); }, "RangeNotHonored");
    }

    private static async Task StreamLengths()
    {
        foreach (var (delta, code) in new[] { (-1, "TruncatedDownload"), (1, "LengthMismatch") })
        {
            using var server = new FakeBaidu { DownloadLengthDelta = delta }; server.Files["/object"] = [1, 2, 3];
            await using var client = Client(server);
            await Error(async () => { await using var stream = await client.OpenReadAsync("/object"); await stream.CopyToAsync(Stream.Null); }, code);
        }
    }

    private static async Task Delete()
    {
        using var server = new FakeBaidu(); server.Files["/object"] = [1]; await using var client = Client(server);
        await client.DeleteAsync("/object");
        Assert(server.TaskPolls == 2 && !server.Files.ContainsKey("/object"), "Delete returned before completion");
        var prior = server.TaskPolls; await client.DeleteAsync("/object"); Assert(server.TaskPolls == prior, "Absent object was deleted again");
    }

    private static async Task DeleteFailure()
    {
        using (var server = new FakeBaidu { TaskFails = true })
        {
            server.Files["/object"] = [1]; await using var client = Client(server);
            await Error(() => client.DeleteAsync("/object"), "DeleteFailed");
        }
        using (var server = new FakeBaidu { TaskLeavesObject = true })
        {
            server.Files["/object"] = [1]; await using var client = Client(server);
            await Error(() => client.DeleteAsync("/object"), "DeleteNotConfirmed");
        }
        using (var server = new FakeBaidu { TaskItemError = true })
        {
            server.Files["/object"] = [1]; await using var client = Client(server);
            await Error(() => client.DeleteAsync("/object"), "Baidu:12");
        }
    }

    private static async Task Redirect()
    {
        using var server = new FakeBaidu { EvilRedirect = true }; server.Files["/object"] = [1];
        await using var client = Client(server);
        await Error(async () => { await using var stream = await client.OpenReadAsync("/object"); }, "UntrustedRedirect");
        Assert(server.Calls.All(c => c.Host != "example.com"), "Redirect credentials escaped allowlist");
    }

    private static async Task HttpsUpgrade()
    {
        var data = Encoding.UTF8.GetBytes("TLS only signed URI check");
        using (var server = new FakeBaidu { AdvertiseHttp = true })
        {
            await using var client = Client(server);
            await client.PutImmutableAsync("/object + escaped", new MemoryStream(data), data.Length, Sha(data));
            await using (var download = await client.OpenReadAsync("/object + escaped")) await download.CopyToAsync(Stream.Null);
            Assert(server.UploadedParts == 1 && server.Downloads == 1, "HTTP-advertised endpoints were not usable over HTTPS");
            Assert(server.Calls.All(c => c.Scheme == "https"), "A cleartext HTTP request was emitted");
            Assert(server.DownloadQueries.Single().Contains("signature=a%2Fb%2Bc%3D&repeat=x%20y", StringComparison.Ordinal), "Signed query escaping changed");
        }
        foreach (var address in new[] { "http://download.baidupcs.com:8080/content", "http://user@download.baidupcs.com/content", "http://example.com/content" })
        {
            using var server = new FakeBaidu { DownloadAddress = address }; server.Files["/object"] = data;
            await using var client = Client(server);
            await Error(async () => { await using var ignored = await client.OpenReadAsync("/object"); }, "MissingDownloadLink");
            Assert(server.Downloads == 0 && server.Calls.All(c => c.Scheme == "https"), "An unsafe HTTP endpoint was requested");
        }
    }

    private static async Task Vault()
    {
        if (!OperatingSystem.IsWindows()) return;
        var folder = Path.Combine(Path.GetTempPath(), "OverlayDisk-vault-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var file = Path.Combine(folder, "session.dpapi");
        try
        {
            await WindowsSessionVault.SaveAsync(file, Session);
            var bytes = await File.ReadAllBytesAsync(file);
            Assert(!Encoding.UTF8.GetString(bytes).Contains("fake-test-session-only"), "Plaintext cookie on disk");
            var recovered = await WindowsSessionVault.LoadAsync(file);
            Assert(recovered?.Cookies.SequenceEqual(Session.Cookies) == true, "Cookie attributes did not round-trip");
            bytes[^5] ^= 0x45; await File.WriteAllBytesAsync(file, bytes);
            try { _ = await WindowsSessionVault.LoadAsync(file); throw new Exception("Tampered DPAPI accepted"); }
            catch (CryptographicException) { }
            await WindowsSessionVault.SaveAsync(file, Session);
            WindowsSessionVault.Delete(file);
            Assert(await WindowsSessionVault.LoadAsync(file) is null, "Session deletion failed");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static int Calls(FakeBaidu server, string operation) => server.Calls.Count(c => operation switch
    {
        "account" => c.Path == "/rest/2.0/membership/user/info",
        "list" => c.Path == "/rest/2.0/xpan/file",
        "locate-upload" => c.Path == "/rest/2.0/pcs/file" && c.Query.Contains("method=locateupload"),
        "locate-download" => c.Path == "/rest/2.0/pcs/file" && c.Query.Contains("method=locatedownload"),
        "part" => c.Path == "/rest/2.0/pcs/superfile2",
        "precreate" => c.Path == "/api/precreate",
        "create" => c.Path == "/api/create",
        "delete" => c.Path == "/api/filemanager",
        "poll" => c.Path == "/share/taskquery",
        "download" => c.Path == "/content",
        _ => false
    });
    private static BaiduClient Cached(FakeBaidu server, string? cache = null, int capacity = 8192, TimeProvider? clock = null, int attempts = 2) =>
        new(Session, new BorrowedHandler(server), new BaiduClientOptions
        {
            RequestScheduler = TestScheduler,
            AssumeExclusiveWriter = true, MetadataCacheDirectory = cache, MaximumCachedMetadataEntries = capacity,
            MaximumAttempts = attempts, MaximumTaskPolls = 3, RetryDelay = TimeSpan.Zero, TaskPollDelay = TimeSpan.Zero,
            TimeProvider = clock ?? TimeProvider.System
        });
    private static string CacheFolder() => Path.Combine(Path.GetTempPath(), "OverlayDisk-metadata-tests-" + Guid.NewGuid().ToString("N"));
    private static Task<CloudObjectInfo> Put(BaiduClient client, string path, byte[] data) => client.PutImmutableAsync(path, new MemoryStream(data, false), data.Length, Sha(data));

    private static async Task CacheRequestCounts()
    {
        using var server = new FakeBaidu(); await using var client = Cached(server);
        await client.CreateDirectoryAsync("/bucket");
        Assert(server.Calls.Count == 7 && Calls(server, "account") == 1 && Calls(server, "list") == 1, "Cold directory setup request count differs");
        Assert(client.GetRequestCounts().Values.Sum() == 7, "Operation-only request counters differ from actual sends");
        server.Calls.Clear(); await client.CreateDirectoryAsync("/bucket");
        Assert(server.Calls.Count == 0, "Warm known directory made requests");
        byte[] a = [1, 2, 3]; byte[] b = [4, 5, 6];
        await Put(client, "/bucket/a", a);
        Assert(server.Calls.Count == 4 && Calls(server, "list") == 0 && Calls(server, "locate-upload") == 1 && Calls(server, "download") == 0, "First object should use only precreate/locate/part/create");
        server.Calls.Clear(); await Put(client, "/bucket/b", b);
        Assert(server.Calls.Count == 3 && Calls(server, "list") == 0 && Calls(server, "locate-upload") == 0, "Warm increment should use only precreate/part/create");
        server.Calls.Clear(); var reused = await Put(client, "/bucket/a", a);
        Assert(reused.ReusedExisting, "Cache reuse was reported as new network transfer");
        Assert(server.Calls.Count == 0, "Verified immutable repeat made requests");
        await Error(async () => _ = await Put(client, "/bucket/a", b), "ObjectConflict");
        Assert(server.Calls.Count == 0 && server.Files["/bucket/a"].SequenceEqual(a), "Cached conflict overwrote or made unnecessary requests");
        _ = await client.HeadAsync("/bucket/missing"); Assert(server.Calls.Count == 0, "Complete directory absence proof was ignored");
        await foreach (var _ in client.ListAsync("/bucket")) { }
        await foreach (var _ in client.ListAsync("/bucket")) { }
        Assert(Calls(server, "list") == 2, "Explicit reader listings were cached");
    }

    private static async Task CacheRestart()
    {
        var folder = CacheFolder(); using var server = new FakeBaidu();
        try
        {
            await using (var first = Cached(server, folder))
            {
                await first.CreateDirectoryAsync("/bucket");
                for (byte i = 0; i < 3; i++) await Put(first, "/bucket/object" + i, [i, 7]);
            }
            server.Calls.Clear();
            await using (var reopened = Cached(server, folder, capacity: 1))
            {
                await reopened.CreateDirectoryAsync("/bucket");
                for (byte i = 0; i < 3; i++) await Put(reopened, "/bucket/object" + i, [i, 7]);
                Assert(server.Calls.Count == 1 && Calls(server, "account") == 1, "Restart/RAM eviction discarded durable metadata proofs");
                await Put(reopened, "/bucket/new-object", [9, 7]);
                Assert(server.Calls.Count == 9 && Calls(server, "list") == 0, "Restart increment should require account/session + four upload calls, no LIST");
            }
            Console.WriteLine("COUNTS cold-directory=7 first-object=4 warm-increment=3 repeat=0 restart-three-repeats=1 restart-new-increment-total=9");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task CacheCorruptionAndScope()
    {
        var folder = CacheFolder(); using var server = new FakeBaidu();
        try
        {
            await using (var initial = Cached(server, folder)) { await initial.CreateDirectoryAsync("/bucket"); await Put(initial, "/bucket/a", [1, 2]); }
            var index = Directory.GetFiles(folder, Sha(Encoding.UTF8.GetBytes("/bucket")) + ".cache", SearchOption.AllDirectories).Single();
            var bytes = await File.ReadAllBytesAsync(index); bytes[^2] ^= 1; await File.WriteAllBytesAsync(index, bytes);
            server.Calls.Clear();
            await using (var repaired = Cached(server, folder))
            {
                Assert(await repaired.HeadAsync("/bucket/a") is not null, "Corrupt cache hid a real object");
                Assert(Calls(server, "account") == 1 && Calls(server, "list") == 1, "Corrupt cache was trusted or listed repeatedly");
            }
            server.Calls.Clear();
            await using (var refreshedBeforeOpen = Cached(server, folder))
            {
                await refreshedBeforeOpen.InvalidateCachesAsync();
                _ = await refreshedBeforeOpen.HeadAsync("/bucket/a");
                Assert(Calls(server, "list") == 1, "Refresh before lazy cache initialization ignored persisted indexes");
            }
            server.AccountId = 4321; server.Calls.Clear();
            await using (var switched = Cached(server, folder))
            {
                _ = await switched.HeadAsync("/bucket/a");
                Assert(Calls(server, "account") == 1 && Calls(server, "list") == 1, "Cache crossed validated account scope");
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task CacheUploadFailures()
    {
        var folder = CacheFolder(); using var server = new FakeBaidu();
        try
        {
            await using (var client = Cached(server, folder))
            {
                await client.CreateDirectoryAsync("/bucket");
                server.LoseCommitReply = true; server.Calls.Clear();
                await Put(client, "/bucket/recovered", [1, 9]);
                Assert(Calls(server, "create") == 1 && Calls(server, "list") == 1 && Calls(server, "download") == 1, "Lost create response was not resolved once with fresh SHA verification");
                server.Calls.Clear(); await Put(client, "/bucket/recovered", [1, 9]); Assert(server.Calls.Count == 0, "Recovered receipt was not cached");
                server.BadPartMd5 = true;
                await Error(async () => _ = await Put(client, "/bucket/failed", [3, 7]), "UploadChecksumMismatch");
                server.BadPartMd5 = false;
            }
            server.Calls.Clear();
            await using (var reopened = Cached(server, folder))
            {
                Assert(await reopened.HeadAsync("/bucket/failed") is null && Calls(server, "list") == 1, "Failed mutation left a durable negative proof");
                server.MissingCreateFields = true; server.Calls.Clear();
                var fallback = await Put(reopened, "/bucket/fallback", [6, 7]);
                Assert(!fallback.ReusedExisting, "New create acknowledgment was reported as reuse");
                Assert(Calls(server, "download") == 0 && Calls(server, "list") == 0, "Successful incomplete create performed a redundant download or LIST");
                server.MissingCreateFields = false;
            }
            await using (var strict = Cached(server, folder))
            {
                server.LoseCommitReply = true; server.DownloadLengthDelta = -1; server.Calls.Clear();
                await Error(async () => _ = await Put(strict, "/bucket/truncated", [8, 9]), "TruncatedDownload");
                Assert(Calls(server, "create") == 1, "A truncated verification re-created an already confirmed remote path");
                server.DownloadLengthDelta = 0;
            }
            server.Calls.Clear();
            await using (var recovered = Cached(server, folder))
            {
                await Put(recovered, "/bucket/truncated", [8, 9]);
                Assert(Calls(server, "list") == 1 && Calls(server, "download") == 1 && Calls(server, "create") == 0,
                    "A truncated verification left a durable SHA proof or overwrote the remote object");
            }
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task BatchDelete()
    {
        using var server = new FakeBaidu(); await using var client = Cached(server);
        await client.InitializeWebSessionAsync();
        var paths = Enumerable.Range(0, 65).Select(i => "/object" + i).ToArray();
        foreach (var path in paths) server.Files[path] = [1];
        await foreach (var _ in client.ListAsync("/")) { }
        server.Calls.Clear();
        await client.DeleteManyAsync(paths);
        Assert(server.Files.Count == 0 && Calls(server, "delete") == 2 && Calls(server, "poll") == 4 && Calls(server, "list") == 2 && server.Calls.Count == 8,
            "65 known objects should use two bounded delete tasks and one fresh confirmation per parent/batch");
        server.Calls.Clear(); await client.DeleteManyAsync(paths); Assert(server.Calls.Count == 0, "Already-confirmed absent batch made requests");
        server.Files["/lost-reply"] = [2]; await foreach (var _ in client.ListAsync("/")) { }
        server.Calls.Clear(); server.LoseDeleteReply = true; await client.DeleteManyAsync(["/lost-reply"]);
        Assert(Calls(server, "delete") == 1 && Calls(server, "list") == 1 && Calls(server, "poll") == 0, "Lost deletion reply was not confirmed by a fresh listing");
        Console.WriteLine("COUNTS warm-delete-65=8 (delete=2,poll=4,LIST=2) lost-delete-reply=2");
    }

    private static async Task CacheTtl()
    {
        var clock = new FakeClock(); using var server = new FakeBaidu(); await using var client = Cached(server, clock: clock);
        _ = await client.ValidateAsync(); await client.InitializeWebSessionAsync(); _ = await client.ValidateAsync();
        Assert(Calls(server, "account") == 1, "Account validation was duplicated within TTL");
        await client.CreateDirectoryAsync("/bucket"); await Put(client, "/bucket/a", [1]); await Put(client, "/bucket/b", [2]);
        Assert(Calls(server, "locate-upload") == 1, "Upload endpoint was not reused");
        clock.Now += TimeSpan.FromMinutes(6); _ = await client.ValidateAsync(); await Put(client, "/bucket/c", [3]);
        Assert(Calls(server, "account") == 2 && Calls(server, "locate-upload") == 2, "Expired account/endpoint cache was reused");
        await client.InvalidateCachesAsync(); server.Calls.Clear(); _ = await client.HeadAsync("/bucket/a");
        Assert(Calls(server, "list") == 1, "Explicit refresh trusted stale metadata");
    }

    private static async Task<int> CacheCrashChild(string folder)
    {
        using var server = new FakeBaidu();
        server.Override = async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath != "/api/create") return null;
            Console.WriteLine("cache-invalidated-before-remote-commit"); Console.Out.Flush();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        };
        await using var client = Cached(server, folder);
        await Put(client, "/bucket/crash", [5, 9]);
        return 2;
    }

    private static async Task CacheCrash()
    {
        var folder = CacheFolder(); using var server = new FakeBaidu();
        try
        {
            await using (var initial = Cached(server, folder)) { await initial.CreateDirectoryAsync("/bucket"); await Put(initial, "/bucket/anchor", [1]); }
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--cache-crash-helper"); start.ArgumentList.Add(folder);
            using var child = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start cache crash helper");
            try
            {
                var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert(line == "cache-invalidated-before-remote-commit", "Crash helper did not reach the mutation window");
                child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            // Model the server completing an already received create after the
            // client process dies, without running the client's failure finally.
            server.Files["/bucket/crash"] = [5, 9]; server.Calls.Clear();
            await using var reopened = Cached(server, folder);
            Assert(await reopened.HeadAsync("/bucket/crash") is not null && Calls(server, "list") == 1, "Restart trusted a pre-mutation negative proof after process kill");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task CacheParallel()
    {
        var folder = CacheFolder(); using var server = new FakeBaidu();
        try
        {
            await using (var client = Cached(server, folder, attempts: 1))
            {
                await client.CreateDirectoryAsync("/bucket");
                var arrived = 0;
                var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                server.Override = async (request, ct) =>
                {
                    if (request.RequestUri!.AbsolutePath != "/rest/2.0/pcs/superfile2") return null;
                    if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult();
                    await both.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    return Uri.UnescapeDataString(request.RequestUri.Query).Contains("path=/bucket/fail", StringComparison.Ordinal)
                        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
                };
                var success = Put(client, "/bucket/ok", [1, 2]);
                var failure = Error(async () => _ = await Put(client, "/bucket/fail", [3, 4]), "Http:503");
                await Task.WhenAll(success, failure);
                Assert(server.Files.ContainsKey("/bucket/ok") && !server.Files.ContainsKey("/bucket/fail"), "Parallel test outcomes differ");
            }
            server.Override = null; server.Calls.Clear();
            await using var recovered = Cached(server, folder);
            Assert(await recovered.HeadAsync("/bucket/ok") is not null && await recovered.HeadAsync("/bucket/fail") is null,
                "Partial parallel mutation state was cached as complete");
            Assert(Calls(server, "list") == 1, "Failed parallel scope did not invalidate durable parent proof");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static async Task DirectoryCreationRace()
    {
        using var server = new FakeBaidu(); await using var client = Cached(server, attempts: 1);
        var child = "/bucket/child";
        while ((uint)StringComparer.Ordinal.GetHashCode(child) % 64 == (uint)StringComparer.Ordinal.GetHashCode("/bucket") % 64) child += "x";
        var failed = "/bucket/failure";
        while ((uint)StringComparer.Ordinal.GetHashCode(failed) % 64 == (uint)StringComparer.Ordinal.GetHashCode("/bucket") % 64) failed += "x";
        var handled = false;
        server.Override = async (request, ct) =>
        {
            if (handled || request.RequestUri!.AbsolutePath != "/api/create" || request.Content is null) return null;
            var body = await request.Content.ReadAsStringAsync(ct);
            if (!body.Contains("isdir=1", StringComparison.Ordinal)) return null;
            handled = true; server.SeedDirectory("/bucket");
            // Cloud mkdir exists, but its response has not reached the creator.
            await Put(client, child, [7]);
            server.BadPartMd5 = true;
            await Error(async () => _ = await Put(client, failed, [8]), "UploadChecksumMismatch");
            server.BadPartMd5 = false;
            return FakeBaidu.Raw("{\"errno\":0,\"path\":\"/bucket\",\"size\":0,\"isdir\":1,\"fs_id\":77}");
        };
        await client.CreateDirectoryAsync("/bucket");
        server.Calls.Clear();
        Assert(await client.HeadAsync(child) is not null && Calls(server, "list") == 1,
            "Late mkdir acknowledgment replaced an uncertain/nonempty directory by an empty proof");
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class BorrowedHandler(HttpMessageHandler handler) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker inner = new(handler, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => inner.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    internal sealed record Call(string Host, string Path, string Cookie, string Body, string Scheme, string Query);
    internal sealed class FakeBaidu : HttpMessageHandler
    {
        private readonly SemaphoreSlim requestGate = new(1, 1);
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = ["/"];
        public void SeedDirectory(string path) => directories.Add(path);
        private readonly Dictionary<string, SortedDictionary<int, byte[]>> parts = [];
        public List<Call> Calls { get; } = [];
        public int UploadedParts, PartAttempts, Downloads, TaskPolls;
        public long AccountId = 1234;
        public bool RepeatPages, LoseCommitReply, FailFirstPart, BadPartMd5, OpaqueMd5, IgnoreRange, TaskFails, TaskLeavesObject, TaskItemError, EvilRedirect, AdvertiseHttp, HtmlPartJson;
        public bool MissingCreateFields, LoseDeleteReply, MissingPartMd5;
        public Func<string, long, HttpResponseMessage>? CreateReply;
        public string? DownloadAddress;
        public List<string> DownloadQueries { get; } = [];
        public int DownloadLengthDelta;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Override;
        private string[] deleting = [];
        public static HttpResponseMessage Raw(string value, string type = "application/json") => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, type) };
        private static HttpResponseMessage Json(object value) => Raw(JsonSerializer.Serialize(value));
        private static Dictionary<string, string> Parse(string text) => text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)).ToDictionary(x => WebUtility.UrlDecode(x[0]), x => WebUtility.UrlDecode(x.Length == 2 ? x[1] : ""));
        private object Entry(string path, bool dir) => new { path, size = dir ? 0 : Files[path].Length, isdir = dir ? 1 : 0,
            fs_id = Math.Abs((long)path.GetHashCode()), md5 = dir ? "" : OpaqueMd5 ? "opaque-checksum" : Md5(Files[path]), server_mtime = 1700000000 };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = Parse(uri.Query);
            var body = request.Content is FormUrlEncodedContent ? await request.Content.ReadAsStringAsync(cancellationToken) : "";
            var form = Parse(body);
            lock (Calls) Calls.Add(new Call(uri.Host, uri.AbsolutePath, request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join(';', cookie) : "", body, uri.Scheme, uri.Query));
            if (Override is not null && await Override(request, cancellationToken) is { } replaced) return replaced;
            await requestGate.WaitAsync(cancellationToken);
            try
            {
            switch (uri.AbsolutePath)
            {
                case "/rest/2.0/membership/user/info": return Json(new { errno = 0, user_info = new { uk = AccountId, username = "Test User", is_svip = 0 }, total = 1000000, used = 100 });
                case "/disk/home": return Raw("<html>disk</html>", "text/html");
                case "/api/loginStatus":
                    var login = Json(new { errno = 0, login_info = new { bdstoken = "token-for-test" } });
                    login.Headers.TryAddWithoutValidation("Set-Cookie", "pcsett=test-setting; Path=/; Secure; HttpOnly"); return login;
                case "/api/gettemplatevariable": return Json(new { errno = 0, result = new { bdstoken = "token-for-test" } });
                case "/pcloud/user/getinfo": return Json(new { errno = 0, user_info = new { uk = 1234 } });
                case "/rest/2.0/xpan/file":
                    var dir = query["dir"];
                    if (!directories.Contains(dir)) return Json(new { errno = -9 });
                    string Parent(string path) { var index = path.LastIndexOf('/'); return index == 0 ? "/" : path[..index]; }
                    var names = Files.Keys.Concat(directories.Where(x => x != "/")).Where(x => Parent(x) == dir).Order(StringComparer.Ordinal).ToArray();
                    if (query.GetValueOrDefault("desc") == "1") Array.Reverse(names);
                    var page = RepeatPages ? 1 : int.Parse(query["page"]); var size = int.Parse(query["num"]);
                    return Json(new { errno = 0, list = names.Skip((page - 1) * size).Take(size).Select(x => Entry(x, directories.Contains(x))).ToArray() });
                case "/api/precreate":
                    var id = "upload-" + parts.Count; parts[id] = [];
                    var count = JsonSerializer.Deserialize<string[]>(form["block_list"])!.Length;
                    return Json(new { errno = 0, return_type = 1, uploadid = id, block_list = Enumerable.Range(0, count).ToArray() });
                case "/rest/2.0/pcs/superfile2":
                    PartAttempts++;
                    if (FailFirstPart && PartAttempts == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    var multipart = (MultipartFormDataContent)request.Content!;
                    var part = await multipart.Single().ReadAsByteArrayAsync(cancellationToken);
                    parts[query["uploadid"]][int.Parse(query["partseq"])] = part; UploadedParts++;
                    var partResult = MissingPartMd5 ? Json(new { errno = 0 }) : Json(new { md5 = BadPartMd5 ? new string('0', 32) : Md5(part), request_id = 1 });
                    if (HtmlPartJson) partResult.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
                    return partResult;
                case "/api/create":
                    var createdPath = form["path"];
                    if (form["isdir"] == "1") { directories.Add(createdPath); return Json(new { errno = 0, path = createdPath, size = 0, isdir = 1, fs_id = 77 }); }
                    Assert(form["rtype"] == "0", "Overwrite in request");
                    if (Files.ContainsKey(createdPath)) return Json(new { errno = -8 });
                    Files[createdPath] = parts[form["uploadid"]].Values.SelectMany(x => x).ToArray();
                    if (LoseCommitReply) { LoseCommitReply = false; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
                    if (MissingCreateFields) return Json(new { errno = 0, path = createdPath });
                    if (CreateReply is not null) return CreateReply(createdPath, Files[createdPath].Length);
                    return Json(new { errno = 0, path = createdPath, size = Files[createdPath].Length, isdir = 0, fs_id = Math.Abs((long)createdPath.GetHashCode()),
                        md5 = OpaqueMd5 ? "opaque-checksum" : Md5(Files[createdPath]) });
                case "/rest/2.0/pcs/file":
                    if (query["method"] == "locateupload") return Json(new { servers = new[] { new { server = AdvertiseHttp ? "http://upload.pcs.baidu.com:80" : "https://upload.pcs.baidu.com" } } });
                    Assert(query["method"] == "locatedownload", "Unknown PCS method");
                    return Json(new { urls = new[] { new { encrypt = 0, url = (DownloadAddress ?? (AdvertiseHttp ? "http://download.baidupcs.com:80/content" : "https://download.baidupcs.com/content")) + "?path=" + Uri.EscapeDataString(query["path"]) + "&signature=a%2Fb%2Bc%3D&repeat=x%20y" } } });
                case "/content":
                    Downloads++;
                    DownloadQueries.Add(uri.Query);
                    if (EvilRedirect) { var redirected = new HttpResponseMessage(HttpStatusCode.Redirect); redirected.Headers.Location = new Uri("https://example.com/stolen"); return redirected; }
                    var file = Files[query["path"]];
                    if (request.Headers.Range is { } range && !IgnoreRange)
                    {
                        var one = range.Ranges.Single(); var start = one.From!.Value; var end = one.To!.Value;
                        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(file[(int)start..(int)(end + 1)]) };
                        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, file.Length); return response;
                    }
                    if (DownloadLengthDelta != 0)
                    {
                        var changed = new byte[file.Length + DownloadLengthDelta]; file.AsSpan(0, Math.Min(file.Length, changed.Length)).CopyTo(changed);
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ForwardStream(changed)) };
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) };
                case "/api/filemanager":
                    deleting = JsonSerializer.Deserialize<string[]>(form["filelist"])!; TaskPolls = 0;
                    if (LoseDeleteReply) { foreach (var path in deleting) Files.Remove(path); return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
                    return Json(new { errno = 0, taskid = 77 });
                case "/share/taskquery":
                    TaskPolls++;
                    if (TaskFails) return Json(new { errno = 0, status = "failed" });
                    if (TaskItemError) return Json(new { errno = 0, status = "success", list = new[] { new { errno = 12 } } });
                    if (TaskPolls < 2) return Json(new { errno = 0, status = "pending" });
                    if (!TaskLeavesObject) foreach (var path in deleting) Files.Remove(path);
                    return Json(new { errno = 0, status = "success" });
                default: throw new Exception("Unexpected test route: " + uri.AbsolutePath);
            }
            }
            finally { requestGate.Release(); }
        }
    }

    private sealed class ForwardStream(byte[] data) : Stream
    {
        private readonly MemoryStream inner = new(data, false);
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
