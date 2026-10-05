using System.Security.Cryptography;
using System.Text.Json;

namespace OverlayDisk;

/// <summary>Explicit administrator-only acceptance against newly created fixture volumes.</summary>
public static class ObjectSizeMountSelfTests
{
    public static async Task RunAsync(string outputDirectory)
    {
        outputDirectory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(outputDirectory);
        string fixture = Path.Combine(outputDirectory, "owned-volumes");
        if (Directory.Exists(fixture) || File.Exists(fixture)) throw new IOException("测试目录已有内容，请使用新的输出目录。");
        Directory.CreateDirectory(fixture);
        var checks = new List<string>(); var failures = new List<string>();
        void Log(string stage) => File.AppendAllText(Path.Combine(outputDirectory, "progress.log"), DateTimeOffset.Now.ToString("O") + " " + stage + Environment.NewLine);
        using var controller = new DiskController(Path.Combine(fixture, "catalog"));
        char Letter() => Enumerable.Range('D', 'Z' - 'D' + 1).Select(value => (char)value)
            .First(value => !DriveInfo.GetDrives().Any(drive => char.ToUpperInvariant(drive.Name[0]) == value));
        const string password = "isolated-object-size-mount-fixture";
        async Task VerifyFileAsync(DiskEntry entry, string? secret)
        {
            string file = entry.DriveLetter + @":\object-size-fixture.bin";
            byte[] expected = new byte[1024 * 1024 + 123]; RandomNumberGenerator.Fill(expected);
            await File.WriteAllBytesAsync(file, expected).ConfigureAwait(false);
            string hash = Convert.ToHexString(SHA256.HashData(expected));
            Log($"{entry.Name}: wrote fixture file; safely unmounting");
            await controller.UnmountAsync(entry).ConfigureAwait(false);
            await controller.MountAsync(entry, secret).ConfigureAwait(false);
            byte[] actual = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            Check(Convert.ToHexString(SHA256.HashData(actual)) == hash, "file hash changed after remount");
            var core = controller.TryGetCore(entry.Id) ?? throw new IOException("Fixture core is not mounted.");
            Check(core.ObjectSizeBytes == entry.ObjectSizeBytes, "runtime and persisted object size differ");
            await controller.UnmountAsync(entry).ConfigureAwait(false);
            using var reopened = new CoreDisk(entry.ContainerPath, secret);
            Check(reopened.ObjectSizeBytes == entry.ObjectSizeBytes && reopened.Id.ToString() == entry.Id, "container geometry/identity changed on reopen");
            checks.Add($"{entry.ObjectSizeBytes / (1024 * 1024)} MiB objects: NTFS creation, file hash after safe remount, native reopen and object size verified.");
        }
        try
        {
            foreach (uint size in new uint[] { 4u << 20, 8u << 20, 16u << 20 })
            {
                string path = Path.Combine(fixture, "object-" + size / (1024 * 1024) + "m.odv4");
                Log($"creating new encrypted {size / (1024 * 1024)} MiB object fixture");
                await controller.CreateAsync(new("Object fixture " + size / (1024 * 1024), path, 256UL << 20, Letter(), true, false, size), password).ConfigureAwait(false);
                var entry = controller.Disks.Single(disk => string.Equals(disk.ContainerPath, path, StringComparison.OrdinalIgnoreCase));
                Check(entry.Initialized && entry.Mounted && entry.ObjectSizeBytes == size, "created entry not initialized with selected object size");
                await VerifyFileAsync(entry, password).ConfigureAwait(false);
            }

            // Simulate precisely the durable state left by successful Initialize-Disk
            // followed by the former empty-partition query failure. Only this newly
            // created file is written, never an existing registered disk.
            string interrupted = Path.Combine(fixture, "interrupted-empty-mbr.odv4");
            Log("creating isolated interrupted empty MBR fixture");
            CoreDisk.Create(interrupted, 256UL << 20, null);
            Guid identity;
            using (var core = new CoreDisk(interrupted, null))
            {
                identity = core.Id; byte[] mbr = new byte[512];
                RandomNumberGenerator.Fill(mbr.AsSpan(440, 4)); mbr[510] = 0x55; mbr[511] = 0xAA;
                core.Write(0, mbr, mbr.Length); core.Flush();
                Check(DiskInitializationProof.IsEmptyInitialized(core), "empty MBR proof rejected the interrupted fixture");
            }
            await controller.ImportAsync(interrupted).ConfigureAwait(false);
            var retry = controller.Disks.Single(disk => disk.Id == identity.ToString());
            Check(!retry.Initialized, "interrupted entry incorrectly marked initialized");
            Log("mounting preserved empty MBR; continuing partition creation");
            await controller.MountAsync(retry, null).ConfigureAwait(false);
            Check(retry.Initialized && retry.Id == identity.ToString(), "safe retry did not preserve container identity");
            await VerifyFileAsync(retry, null).ConfigureAwait(false);
            checks.Add("A preserved MBR-only container safely resumes partition creation and formatting, keeps its UUID, and survives file verification/remount.");
        }
        catch (Exception failure) { failures.Add(failure.ToString()); throw; }
        finally
        {
            Log("safely closing all fixture volumes");
            try { await controller.UnmountAllAsync().ConfigureAwait(false); }
            catch (Exception failure) { failures.Add("Cleanup: " + failure); }
            File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(new { passed = failures.Count == 0, checks, failures }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (failures.Count != 0) throw new IOException(string.Join(Environment.NewLine, failures));
        Log("completed; all fixture volumes closed");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new IOException("Object-size mount fixture: " + message); }
}
