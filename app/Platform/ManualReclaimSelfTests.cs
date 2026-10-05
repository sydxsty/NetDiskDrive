using System.Text.Json;

namespace OverlayDisk;

/// <summary>Pure allocation-bitmap tests: no disk handles, native engine, driver or user settings.</summary>
internal static class ManualReclaimSelfTests
{
    internal static IReadOnlyList<string> Run()
    {
        var passed = new List<string>();
        passed.AddRange(NtfsAllocationBitmapSelfTests.Run());
        CheckPage(Bitmap(0, 10, [0b1010_0101, 0b1111_1101]), 18, 0, 10);
        CheckPage(Bitmap(0, 24, [0, 255, 0]), 19, 0, 24);
        CheckPage(Bitmap(0, 16, [255, 255]), 18, 0, 16);
        passed.Add("Allocated clusters excluded, zero runs merged, and padding beyond the final cluster ignored.");

        // Microsoft documents this rounded-down StartingLcn example. A partial response
        // advertises all remaining clusters; only the bytes actually returned are usable.
        byte[] rounded = Bitmap(0xA000, 0x33F7, [0, 0xA5, 0, 0xFF]);
        var roundedPage = CheckPage(rounded, rounded.Length, 0xA007, 0xD3F7);
        Assert(roundedPage.Next == 0xA020 && roundedPage.Ranges.All(r => r.Start >= 0xA007), "Rounded-down prefix was returned again.");
        byte[] paddedOutput = Bitmap(0, 64, [0, 255, 0, 0, 0, 0, 0, 0]);
        var shortPage = CheckPage(paddedOutput, 18, 0, 64);
        Assert(shortPage.Next == 16, "The scan read unreturned bytes from an ERROR_MORE_DATA response.");
        passed.Add("Rounded StartingLCN and partial ERROR_MORE_DATA responses advance without rescanning earlier clusters.");

        var random = new Random(0x4E544653);
        for (int sample = 0; sample < 2000; sample++)
        {
            int bytes = random.Next(1, 129);
            ulong start = (ulong)random.Next(0, 10000);
            ulong declared = (ulong)random.Next(1, bytes * 8 + 100);
            byte[] payload = new byte[bytes]; random.NextBytes(payload);
            ulong available = Math.Min(declared, (ulong)bytes * 8);
            ulong requested = start + (ulong)random.Next((int)available);
            CheckPage(Bitmap((long)start, (long)declared, payload), 16 + bytes, requested, start + declared);
        }
        passed.Add("2,000 deterministic mixed-bit pages agree with an independent per-cluster oracle.");

        const int total = 65573;
        byte[] allocation = new byte[(total + 7) / 8]; random.NextBytes(allocation);
        var recovered = new bool[total];
        ulong cursor = 0;
        int pages = 0;
        while (cursor < total)
        {
            ulong start = cursor / 4096 * 4096;
            int bytes = Math.Min(777, allocation.Length - (int)(start / 8));
            byte[] reply = Bitmap((long)start, total - (long)start, allocation.AsSpan((int)(start / 8), bytes).ToArray());
            var page = CheckPage(reply, reply.Length, cursor, total);
            foreach (var range in page.Ranges)
                for (ulong bit = range.Start; bit < range.Start + range.Count; bit++)
                {
                    Assert(!recovered[(int)bit], "Overlapping returned bitmap pages duplicated a free cluster.");
                    recovered[(int)bit] = true;
                }
            cursor = page.Next;
            Assert(++pages < 100, "Paged bitmap scan stopped advancing.");
        }
        for (int bit = 0; bit < total; bit++)
            Assert(recovered[bit] == ((allocation[bit / 8] & (1 << (bit % 8))) == 0), "Paged scan skipped or reclaimed the wrong cluster.");
        passed.Add("Overlapping partial pages cover every free cluster exactly once across a non-byte-aligned volume end.");

        byte[] valid = Bitmap(0, 16, [0, 0]);
        Reject(() => NtfsSpaceReclaimer.DecodeBitmap(valid, 16, 0, 16));
        Reject(() => NtfsSpaceReclaimer.DecodeBitmap(valid, 19, 0, 16));
        Reject(() => NtfsSpaceReclaimer.DecodeBitmap(Bitmap(-1, 16, [0]), 17, 0, 16));
        Reject(() => NtfsSpaceReclaimer.DecodeBitmap(Bitmap(0, -1, [0]), 17, 0, 16));
        Reject(() => NtfsSpaceReclaimer.DecodeBitmap(Bitmap(0, 0, [0]), 17, 0, 16));
        Reject(() => NtfsSpaceReclaimer.DecodeBitmap(Bitmap(8, 8, [0]), 17, 7, 16));
        Reject(() => NtfsSpaceReclaimer.DecodeBitmap(Bitmap(0, 17, [0]), 17, 0, 16));
        Reject(() => NtfsSpaceReclaimer.DecodeBitmap(valid, 18, 16, 16));
        Reject(() => NtfsSpaceReclaimer.DecodeBitmap(valid, 17, 8, 16));
        Reject(() => NtfsSpaceReclaimer.DecodeBitmap(Bitmap(long.MaxValue - 8, 16, [0, 0]), 18, (ulong)long.MaxValue - 8, (ulong)long.MaxValue));
        CheckPage(Bitmap(long.MaxValue - 16, 16, [0, 0]), 18, (ulong)long.MaxValue - 9, (ulong)long.MaxValue);
        passed.Add("Truncated, negative, inconsistent, overflowing-volume and non-progressing responses fail before producing trim ranges.");
        return passed;
    }

    internal static int Run(string output)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new IOException("测试输出目录必须是新的或空目录。");
        Directory.CreateDirectory(output);
        var passed = Run();
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, tests = passed }));
        Console.WriteLine("MANUAL_RECLAIM_BITMAP_SMOKE_OK");
        return 0;
    }

    private static NtfsSpaceReclaimer.BitmapPage CheckPage(byte[] reply, int returned, ulong requested, ulong total)
    {
        var page = NtfsSpaceReclaimer.DecodeBitmap(reply, returned, requested, total);
        ulong start = (ulong)BitConverter.ToInt64(reply, 0);
        ulong end = start + Math.Min((ulong)BitConverter.ToInt64(reply, 8), (ulong)(returned - 16) * 8);
        Assert(page.Next == end, "Bitmap next cursor differs from its returned bytes.");
        var actual = new HashSet<ulong>();
        ulong previousEnd = requested;
        foreach (var range in page.Ranges)
        {
            Assert(range.Count != 0 && range.Start >= previousEnd && range.Start + range.Count <= end, "Free range extends outside the usable bitmap.");
            for (ulong bit = range.Start; bit < range.Start + range.Count; bit++)
                Assert(actual.Add(bit), "Duplicate free cluster in a bitmap page.");
            previousEnd = range.Start + range.Count;
        }
        for (ulong bit = requested; bit < end; bit++)
        {
            ulong index = bit - start;
            bool free = (reply[16 + (int)(index / 8)] & (1 << (int)(index % 8))) == 0;
            Assert(actual.Contains(bit) == free, "Free range differs from the independent bitmap oracle.");
        }
        return page;
    }

    private static byte[] Bitmap(long start, long size, byte[] bits)
    {
        byte[] output = new byte[16 + bits.Length];
        BitConverter.GetBytes(start).CopyTo(output, 0);
        BitConverter.GetBytes(size).CopyTo(output, 8);
        bits.CopyTo(output, 16);
        return output;
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Unsafe bitmap response was accepted.");
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
