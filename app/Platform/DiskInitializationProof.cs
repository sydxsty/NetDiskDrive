namespace OverlayDisk;

/// <summary>Allows resuming only a durable partition-table-only initialization.</summary>
internal static class DiskInitializationProof
{
    internal static bool IsEmptyInitialized(CoreDisk core)
    {
        core.Flush();
        ulong allocated = core.GetInfo().GetProperty("allocated_pages").GetUInt64();
        return Validate(core.Capacity, allocated,
            (offset, length) => { var bytes = new byte[length]; core.Read(offset, bytes, length); return bytes; },
            (start, count) => core.Control(new { cmd = "debug.pages", start_page = start, limit = count })
                .GetProperty("items").EnumerateArray()
                .Where(row => row.GetProperty("object_id").ValueKind != System.Text.Json.JsonValueKind.Null)
                .Select(row => row.GetProperty("page").GetUInt64()).ToArray());
    }

    internal static bool Validate(ulong capacity, ulong allocatedPages, Func<ulong, int, byte[]> read,
        Func<ulong, int, IReadOnlyList<ulong>> mappedPages)
    {
        // A conventional pair of GPT tables occupies at most ten logical pages.
        // Larger allocations are not an empty initialization, regardless of how
        // Windows currently classifies a missing/damaged partition table.
        if (capacity < 64UL << 20 || capacity % 4096 != 0 || allocatedPages > 10) return false;
        if (allocatedPages == 0)
        {
            byte[] first = read(0, 4096);
            return first.Length == 4096 && first.All(value => value == 0);
        }
        var regions = DiskIdentityRewriter.EmptyInitializationRegions(capacity, read);
        if (regions is null) return false;
        var allowedPages = new SortedSet<ulong>();
        foreach (var region in regions)
            for (ulong page = region.Offset / 4096; page < (region.Offset + (ulong)region.Length + 4095) / 4096; page++) allowedPages.Add(page);
        var mapped = new HashSet<ulong>();
        foreach (var page in allowedPages)
            foreach (var actual in mappedPages(page, 1))
            {
                if (actual != page || !mapped.Add(actual)) return false;
            }
        if ((ulong)mapped.Count != allocatedPages) return false;
        foreach (ulong page in mapped)
        {
            byte[] bytes = read(page * 4096, 4096);
            if (bytes.Length != 4096) return false;
            for (int index = 0; index < bytes.Length; index++)
            {
                ulong offset = page * 4096 + (uint)index;
                if (bytes[index] != 0 && !regions.Any(region => offset >= region.Offset && offset - region.Offset < (ulong)region.Length)) return false;
            }
        }
        return true;
    }
}
