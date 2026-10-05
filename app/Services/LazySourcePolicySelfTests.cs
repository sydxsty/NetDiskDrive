using OverlayDisk.Cloud.Sync;
using OverlayDisk.Worker;

namespace OverlayDisk.Services;

/// <summary>Pure values only: no ApplicationService constructor, saved settings, native library or account access.</summary>
internal static class LazySourcePolicySelfTests
{
    private const string Source = "8b4efca9-e0b6-4214-8d48-e93b11ee853a";
    private const string Local = "a0900c60-94b3-491e-9f16-467abbd27b86";
    private const string TaskId = "4e2aff1d-8d72-4010-b4b0-9f1ee08612d9";
    private const string RootObject = "a3ba5d14-2488-406c-ab8c-ab28582aefb7";
    private const string ObjectId = "c5fe5865-62ce-4e63-b5bd-079733aaee66";
    private const string Account = "test-account-scope-only";
    private static string Root => CloudRepository.RootPath(Source);
    private static RestoreRecord Record() => new()
    {
        Id = TaskId, AccountId = Account, RemoteRoot = Root, ReaderPin = Root + "/readers/" + TaskId + ".json",
        LocalDiskId = Local, Lazy = true, Begun = true, Complete = true,
        Commit = new(4, Source, Local, 11, "Pure policy fixture", 64UL * 1024 * 1024, true, RootObject, new string('a', 64), DateTimeOffset.UnixEpoch)
    };
    private static LazyObjectRequest Request() => new(Local, ObjectId, new string('b', 64));
    private static void Reject(RestoreRecord record, LazyObjectRequest? request = null, string? account = Account)
    {
        try { LazySourcePolicy.ValidateRequest(record, request ?? Request(), account); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Invalid lazy source/request scope was accepted.");
    }
    internal static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        LazySourcePolicy.ValidateRequest(Record(), Request(), Account);
        LazySourcePolicy.ValidateRequest(Record(), Request() with { Prefetch = true }, Account);
        var active = Record(); active.Complete = false; active.LocalDiskId = null; active.WorkerId = Local;
        LazySourcePolicy.ValidateRequest(active, Request(), Account);
        passed.Add("Completed local target and active restore worker accept demand/prefetch within their fixed source scope.");

        Reject(Record(), Request() with { DiskId = Source });
        Reject(Record(), Request() with { DiskId = "../disk" });
        var stale = Record(); stale.WorkerId = Source; Reject(stale, Request() with { DiskId = Source });
        var notBegun = Record(); notBegun.Begun = false; Reject(notBegun);
        var full = Record(); full.Lazy = false; Reject(full);
        var deleted = Record(); deleted.ContainerDeleted = true; Reject(deleted);
        passed.Add("Cross-disk, stale completed worker, unstarted, full-restore and deleted-container requests are rejected.");

        foreach (string root in new[] { "/OverlayDisk/v4/" + Source, "/OverlayDisk/" + Local, "/other/" + Source,
            Root + "/..", Root + "/", "/OverlayDisk/../" + Source })
        { var record = Record(); record.RemoteRoot = root; record.ReaderPin = root + "/readers/" + TaskId + ".json"; Reject(record); }
        var source = Record(); source.Commit = source.Commit with { VolumeId = Local }; Reject(source);
        passed.Add("Only /OverlayDisk/{source-id} is recognized; version folders, path escapes and source substitution reject.");

        foreach (string? pin in new[] { null, Root + "/readers/other.json", Root + "/objects/" + TaskId + ".json", Root + "/readers/../" + TaskId + ".json" })
        { var record = Record(); record.ReaderPin = pin; Reject(record); }
        var recordId = Record(); recordId.Id = "../" + TaskId; Reject(recordId);
        var format = Record(); format.Commit = format.Commit with { FormatVersion = 3 }; Reject(format);
        var capacity = Record(); capacity.Commit = capacity.Commit with { CapacityBytes = 4096 }; Reject(capacity);
        var rootIdentity = Record(); rootIdentity.Commit = rootIdentity.Commit with { RootObjectId = "../root" }; Reject(rootIdentity);
        var rootDigest = Record(); rootDigest.Commit = rootDigest.Commit with { RootSha256 = new string('x', 64) }; Reject(rootDigest);
        passed.Add("Reader pins must be the exact fixed task path and source commit identity/version/capacity/digest must be valid.");

        foreach (string hash in new[] { "", new string('a', 63), new string('g', 64), new string('a', 65) })
            Reject(Record(), Request() with { Sha256 = hash });
        Reject(Record(), Request() with { ObjectId = "../" + ObjectId });
        Reject(Record(), Request() with { ObjectId = "{" + ObjectId + "}" });
        Reject(Record(), Request() with { Length = 4096 });
        Reject(Record(), Request() with { Length = CloudRepository.ObjectLength + 1 });
        passed.Add("Object IDs cannot escape their path; requests must match the explicit volume geometry and canonical digest.");

        Reject(Record(), account: null); Reject(Record(), account: "another-account");
        var missingAccount = Record(); missingAccount.AccountId = " "; Reject(missingAccount);
        // A syntactically valid different SHA remains allowed here: this policy is not
        // allowed to replace native root authentication or repository content hashing.
        LazySourcePolicy.ValidateRequest(Record(), Request() with { Sha256 = new string('c', 64) }, Account);
        passed.Add("Missing/wrong account rejects; digest shape validation never claims to authenticate native object membership.");
        return passed;
    }
}
