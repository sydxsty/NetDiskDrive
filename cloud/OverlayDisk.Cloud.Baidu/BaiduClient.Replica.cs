using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Baidu;

public sealed partial class BaiduClient
{
    public async IAsyncEnumerable<CloudObjectInfo> ListByNameDescendingAsync(string directory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        directory = PathValue(directory);
        RequireBduss();
        var cache = await MetadataAsync(cancellationToken).ConfigureAwait(false);
        var revision = cache is null ? 0 : await cache.ListingRevisionAsync(cancellationToken).ConfigureAwait(false);
        string? previousPage = null, previousFile = null;
        for (var page = 1; page <= options.MaximumListPages; page++)
        {
            using var document = await JsonAsync(HttpMethod.Get, Url(Pan, "rest/2.0/xpan/file",
                ("method", "list"), ("dir", directory), ("page", page.ToString(CultureInfo.InvariantCulture)),
                ("num", options.PageSize.ToString(CultureInfo.InvariantCulture)), ("order", "name"), ("desc", "1"), ("web", "1")),
                null, "list-reader-latest", cancellationToken, native: true, missingDirectory: directory).ConfigureAwait(false);
            var root = Data(document.RootElement);
            if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new CloudProviderException("MissingListing", "Baidu did not return a directory listing.");
            if (list.GetArrayLength() > options.PageSize)
                throw new CloudProviderException("MalformedListing", "Baidu returned an oversized listing page.");
            var signature = HashHex(SHA256.HashData(Encoding.UTF8.GetBytes(list.GetRawText())));
            if (list.GetArrayLength() > 0 && signature == previousPage)
                throw new CloudProviderException("RepeatedListing", "Baidu repeated a directory page; enumeration is incomplete.", isTransient: true);
            previousPage = signature;
            var items = new List<CloudObjectInfo>(list.GetArrayLength());
            foreach (var item in list.EnumerateArray())
            {
                var parsed = ParseObject(item);
                if (Parent(parsed.Path) != directory || parsed.Path == directory)
                    throw new CloudProviderException("MalformedListing", "Baidu returned an object outside the requested directory.");
                if (!parsed.IsDirectory)
                {
                    if (previousFile is not null && StringComparer.Ordinal.Compare(previousFile, parsed.Path) <= 0)
                        throw new CloudProviderException("UnorderedListing", "Baidu did not return descending filenames; newest version cannot be selected safely.");
                    previousFile = parsed.Path;
                }
                items.Add(parsed);
            }
            // Record before yielding: consumers often stop within this first page.
            // A partial page never becomes a durable complete-directory absence proof.
            if (cache is not null) await cache.RecordPartialListingAsync(directory, items, revision, cancellationToken).ConfigureAwait(false);
            foreach (var item in items) yield return item;
            if (items.Count < options.PageSize) yield break;
        }
        throw new CloudProviderException("ListingLimit", "The directory listing limit was reached before completion.");
    }
}
