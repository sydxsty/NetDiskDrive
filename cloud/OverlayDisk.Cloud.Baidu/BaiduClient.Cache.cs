using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Baidu;

public sealed partial class BaiduClient
{
    /// <summary>Forget local proofs and session hints before an explicit refresh. Account identity remains bound to this client.</summary>
    public void InvalidateCaches() => InvalidateCachesAsync().GetAwaiter().GetResult();

    public async Task InvalidateCachesAsync(CancellationToken cancellationToken = default)
    {
        BaiduMetadataCache? cache;
        lock (cacheState)
        {
            invalidationGeneration++; accountValidUntil = default; endpointValidUntil = default;
            uploadEndpoint = null; webToken = null; cache = metadataCache;
            invalidatePersistedOnOpen = cache is null;
        }
        if (cache is not null) await cache.InvalidateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<BaiduMetadataCache?> MetadataAsync(CancellationToken cancellationToken)
    {
        if (!options.AssumeExclusiveWriter) return null;
        if (metadataCache is { } existing) return existing;
        await metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (metadataCache is not null) return metadataCache;
            // No persistent account scope is selected from an unvalidated cookie.
            var identity = await ValidateAsync(cancellationToken).ConfigureAwait(false);
            var created = new BaiduMetadataCache(options.MetadataCacheDirectory, ProviderId, identity.AccountId, options.MaximumCachedMetadataEntries);
            try
            {
                for (;;)
                {
                    lock (cacheState)
                    {
                        if (!invalidatePersistedOnOpen) { metadataCache = created; break; }
                        invalidatePersistedOnOpen = false;
                    }
                    await created.InvalidateAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                lock (cacheState) invalidatePersistedOnOpen = true;
                created.Dispose(); throw;
            }
            return metadataCache;
        }
        finally { metadataGate.Release(); }
    }

    private async Task InvalidateForFailureAsync(string operation, CloudProviderException error)
    {
        if (error.AuthenticationRequired) await InvalidateCachesAsync().ConfigureAwait(false);
        else if (operation is "upload-part" or "locate-upload")
            lock (cacheState) { uploadEndpoint = null; endpointValidUntil = default; }
    }
}
