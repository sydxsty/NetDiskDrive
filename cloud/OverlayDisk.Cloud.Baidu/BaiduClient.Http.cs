using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Baidu;

public sealed partial class BaiduClient : ICloudObjectStore, ICloudBatchDeleteStore, ICloudPreparedUploadStore, ICloudKnownObjectReader, ICloudEncodedObjectStore, ICloudBoundedObjectReader
{
    private static readonly Uri Pan = new("https://pan.baidu.com/");
    private static readonly Uri Pcs = new("https://pcs.baidu.com/");
    private readonly HttpClient http;
    private readonly BaiduCookieJar cookies;
    private readonly BaiduClientOptions options;
    private readonly string userAgent;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private readonly SemaphoreSlim accountGate = new(1, 1);
    private readonly SemaphoreSlim endpointGate = new(1, 1);
    private readonly SemaphoreSlim metadataGate = new(1, 1);
    private readonly object cacheState = new();
    private DateTimeOffset accountValidUntil;
    private DateTimeOffset endpointValidUntil;
    private Uri? uploadEndpoint;
    private BaiduMetadataCache? metadataCache;
    private long invalidationGeneration;
    private bool invalidatePersistedOnOpen;
    private readonly ConcurrentDictionary<string, long> requestCounts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim[] paths = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private CloudAccountInfo? account;
    private string? webToken;
    private bool disposed;

    public BaiduClient(BaiduCookieSession session, HttpMessageHandler? handler = null, BaiduClientOptions? options = null)
    {
        this.options = options ?? new BaiduClientOptions();
        if (this.options.MaximumAttempts is < 1 or > 10 || this.options.PageSize is < 1 or > 1000 ||
            this.options.MaximumListPages < 1 || this.options.MaximumTaskPolls < 1 || this.options.RequestTimeout <= TimeSpan.Zero ||
            this.options.MaximumUploadLength <= 0 || this.options.MaximumUploadLength > 4L * 1024 * 1024 * 1024 ||
            this.options.RetryDelay < TimeSpan.Zero || this.options.TaskPollDelay < TimeSpan.Zero ||
            this.options.MaximumCachedMetadataEntries < 1 || this.options.AccountValidationTtl < TimeSpan.Zero ||
            this.options.UploadEndpointTtl < TimeSpan.Zero || this.options.TimeProvider is null || this.options.RequestScheduler is null)
            throw new ArgumentOutOfRangeException(nameof(options));
        cookies = new BaiduCookieJar(session);
        userAgent = session.UserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36";
        if (userAgent.Contains('\r') || userAgent.Contains('\n')) throw new ArgumentException("Invalid User-Agent.", nameof(session));
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string ProviderId => "baidu-private-web";
    /// <summary>HTTP send attempts by fixed operation label; no paths, URLs, account data or per-request log events.</summary>
    public IReadOnlyDictionary<string, long> GetRequestCounts() => new Dictionary<string, long>(requestCounts, StringComparer.Ordinal);
    public BaiduCookieSession ExportSession() => cookies.Export(userAgent);

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        http.Dispose();
        sessionGate.Dispose();
        accountGate.Dispose();
        endpointGate.Dispose();
        metadataGate.Dispose();
        metadataCache?.Dispose();
        foreach (var gate in paths) gate.Dispose();
        await Task.CompletedTask;
    }

    private static Uri Url(Uri origin, string path, params (string Key, string? Value)[] query)
    {
        var builder = new UriBuilder(new Uri(origin, path));
        builder.Query = string.Join('&', query.Where(x => x.Value is not null)
            .Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value!)));
        return builder.Uri;
    }

    private static string PathValue(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/') || path.Contains('\\') || path.Contains('\0') ||
            path.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("An absolute cloud path without '.' or '..' is required.", nameof(path));
        var normalized = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (Encoding.UTF8.GetByteCount(normalized) > 4096) throw new ArgumentException("Cloud path is too long.", nameof(path));
        return normalized;
    }

    private static bool AllowedHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host.Equals("baidu.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".baidu.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("baidupcs.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".baidupcs.com", StringComparison.OrdinalIgnoreCase));

    private static bool TrySecureEndpoint(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || !parsed.IsDefaultPort || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        // Some PCS locate replies still advertise HTTP. Change only the scheme
        // and default port; the signed path/query must keep their escaped form.
        // No request is sent before this normalization and allowlist check.
        if (parsed.Scheme == Uri.UriSchemeHttp)
            parsed = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        if (!AllowedHost(parsed)) return false;
        uri = parsed;
        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, Func<HttpContent?>? body,
        string operation, CancellationToken cancellationToken, CloudByteRange? range = null, bool native = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!TrySecureEndpoint(uri.AbsoluteUri, out uri)) throw new CloudProviderException("UntrustedEndpoint", "Baidu returned an unsupported endpoint.");
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (uri.Host is "passport.baidu.com" or "wappass.baidu.com" ||
                uri.Host == Pan.Host && uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
                throw new CloudProviderException("AuthenticationRequired", "The Baidu session needs to be refreshed in the login window.", authenticationRequired: true);
            using var request = new HttpRequestMessage(method, uri) { Content = body?.Invoke() };
            request.Headers.TryAddWithoutValidation("User-Agent", native ? "softxm;netdisk" : userAgent);
            request.Headers.Referrer = new Uri(Pan, "disk/main");
            var cookie = cookies.Header(uri);
            // PCS API authentication is explicitly BDUSS + the pan service STOKEN.
            // A host-only pan.baidu.com STOKEN is not selected by CookieContainer
            // for upload/pcs nodes. Never forward passport STOKEN or the browser jar.
            // Upstream protocol: BaiduPCS-Rust 427c5c38 client.rs:935-944,1536,1724.
            if (native && operation != "download")
            {
                cookie = "BDUSS=" + RequireBduss();
                if (cookies.Find("STOKEN", Pan) is { Length: > 0 } stoken) cookie += "; STOKEN=" + stoken;
            }
            // The signed PCS download hosts belong to a different registrable
            // domain. Only BDUSS is sent there, never the complete browser jar.
            if ((!native || operation == "download") && uri.Host.EndsWith(".baidupcs.com", StringComparison.OrdinalIgnoreCase) && cookies.Find("BDUSS", uri) is null)
            {
                var bduss = cookies.Find("BDUSS", Pan);
                if (!string.IsNullOrEmpty(bduss)) cookie = string.IsNullOrEmpty(cookie) ? "BDUSS=" + bduss : cookie + "; BDUSS=" + bduss;
            }
            if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            if (range is { } bytes) request.Headers.Range = new RangeHeaderValue(bytes.Offset, checked(bytes.Offset + bytes.Length - 1));
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            IDisposable? admission = await options.RequestScheduler.AcquireAsync(requestCancellation.Token).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation.Token);
            timeout.CancelAfter(options.RequestTimeout);
            HttpResponseMessage response;
            try
            {
                requestCounts.AddOrUpdate(operation, 1, (_, count) => count + 1);
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.Content = new LeasedResponseContent(response.Content, admission, cancellationToken, lifetime.Token);
                admission = null; // The response now owns admission until its body is disposed.
            }
            catch (OperationCanceledException) when (!requestCancellation.IsCancellationRequested)
            { throw new CloudProviderException("RequestTimeout", $"Baidu {operation} timed out.", isTransient: true); }
            catch (HttpRequestException)
            { throw new CloudProviderException("NetworkError", $"Baidu {operation} could not reach the service.", isTransient: true); }
            finally { admission?.Dispose(); }
            cookies.Observe(uri, response);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || method != HttpMethod.Get)
                    throw new CloudProviderException("UnexpectedRedirect", $"Baidu {operation} returned an unexpected redirect.");
                var redirected = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!TrySecureEndpoint(redirected.AbsoluteUri, out uri)) throw new CloudProviderException("UntrustedRedirect", "Baidu redirected to an unsupported host.");
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // Only documented numeric authentication/risk codes refine 401/403.
                // Arbitrary forbidden/HTML replies do not authorize credential replay
                // or endpoint hopping; never include server text in diagnostics.
                CloudProviderException? authentication = null;
                try
                {
                    if (status is 401 or 403) authentication = await ReadAuthenticationFailureAsync(response, requestCancellation.Token).ConfigureAwait(false);
                }
                finally { response.Dispose(); }
                options.Diagnostic?.Invoke(new BaiduDiagnostic(operation, authentication?.Code ?? "HttpError", status));
                if (authentication is not null) throw authentication;
                throw new CloudProviderException("Http:" + status, $"Baidu {operation} returned HTTP {status}.",
                    status is 408 or 429 or >= 500, status is 401);
            }
            return response;
        }
        throw new CloudProviderException("TooManyRedirects", "The Baidu download redirected too many times.");
    }

    private async Task<CloudProviderException?> ReadAuthenticationFailureAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[16 * 1024 + 1]; int length = 0;
            while (length < buffer.Length)
            {
                int read = await body.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                if (read == 0) break; length += read;
            }
            if (length == buffer.Length) return null;
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var value in new[] { json.RootElement, Data(json.RootElement) })
                foreach (string name in new[] { "errno", "error_code" })
                    if (Num(value, name) is long code && code is -6 or 31045 or 132 or 9019) return ApiError(code);
            return null;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException) { return null; }
    }

    private async Task<JsonDocument> JsonAsync(HttpMethod method, Uri uri, Func<HttpContent?>? body,
        string operation, CancellationToken cancellationToken, bool requireErrno = true, bool native = false, bool retry = true)
    {
        try
        {
            return await RetryAsync(async () =>
        {
            using var response = await SendAsync(method, uri, body, operation, cancellationToken, native: native).ConfigureAwait(false);
            using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bodyTimeout.CancelAfter(options.RequestTimeout);
            await using var stream = await response.Content.ReadAsStreamAsync(bodyTimeout.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            for (;;)
            {
                int count;
                try { count = await stream.ReadAsync(buffer, bodyTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
                { throw new CloudProviderException("RequestTimeout", $"Baidu {operation} timed out.", isTransient: true); }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
                { throw new OperationCanceledException(cancellationToken.IsCancellationRequested ? cancellationToken : lifetime.Token); }
                catch (IOException) { throw new CloudProviderException("NetworkError", $"Baidu {operation} response was interrupted.", isTransient: true); }
                if (count == 0) break;
                if (bytes.Length + count > 2 * 1024 * 1024) throw new CloudProviderException("ResponseTooLarge", "The Baidu API response exceeded its allowed size.");
                bytes.Write(buffer, 0, count);
            }
            JsonDocument document;
            var payload = bytes.ToArray();
            // PCS can send valid JSON under text/html. The bounded body and
            // operation-specific success fields, rather than MIME type alone,
            // determine success. Raw response data is never included in errors.
            try { document = JsonDocument.Parse(payload); }
            catch (JsonException)
            {
                var prefix = Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 512)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                if (prefix.StartsWith('<') || response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                    throw new CloudProviderException("UnexpectedHtml", "Baidu returned a web page instead of a successful API response.", authenticationRequired: true);
                throw new CloudProviderException("MalformedResponse", "Baidu returned an invalid API response.");
            }
            try { EnsureSuccess(document.RootElement, requireErrno); return document; }
            catch { document.Dispose(); throw; }
            }, cancellationToken, retry ? options.MaximumAttempts : 1).ConfigureAwait(false);
        }
        catch (CloudProviderException error)
        {
            await InvalidateForFailureAsync(operation, error).ConfigureAwait(false);
            options.Diagnostic?.Invoke(new BaiduDiagnostic(operation, error.Code));
            // The operation is a fixed internal label; no request URL/query,
            // account identifier, provider body or cookie enters this message.
            throw new CloudProviderException(error.Code,
                $"Baidu {operation} failed ({error.Code}).", error.IsTransient, error.AuthenticationRequired);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken.IsCancellationRequested ? cancellationToken : lifetime.Token); }
    }

    private static void EnsureSuccess(JsonElement root, bool required)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new CloudProviderException("MalformedResponse", "Baidu returned a non-object API response.");
        var found = false;
        foreach (var name in new[] { "errno", "error_code" })
        {
            if (!root.TryGetProperty(name, out var code)) continue;
            found = true;
            var value = Number(code) ?? throw new CloudProviderException("MalformedResponse", "Baidu returned an invalid status code.");
            if (value != 0) throw ApiError(value);
        }
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "errno", "error_code" })
            {
                if (!data.TryGetProperty(name, out var nested)) continue;
                found = true;
                var value = Number(nested) ?? throw new CloudProviderException("MalformedResponse", "Baidu returned an invalid nested status.");
                if (value != 0) throw ApiError(value);
            }
        }
        if (required && !found) throw new CloudProviderException("MissingStatus", "Baidu did not confirm successful completion.");
    }

    private static CloudProviderException ApiError(long code) => new("Baidu:" + code.ToString(CultureInfo.InvariantCulture),
        code is -6 or 31045 ? "The Baidu login session has expired." : code is 132 or 9019 ? "Baidu requires verification in its login window." : $"Baidu rejected the request (code {code}).",
        code is 111 or 8002 or 31034,
        code is -6 or 31045 or 132 or 9019);

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken, int? attempts = null)
    {
        var count = attempts ?? options.MaximumAttempts;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await action().ConfigureAwait(false); }
            catch (CloudProviderException error) when (error.IsTransient && attempt < count)
            {
                options.Diagnostic?.Invoke(new BaiduDiagnostic("retry", error.Code));
                await Task.Delay(TimeSpan.FromTicks(options.RetryDelay.Ticks * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static long? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt64(out var n) ? n : null,
        JsonValueKind.String => long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null,
        _ => null
    };
    private static long? Num(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? Number(field) : null;
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field.ValueKind == JsonValueKind.String ? field.GetString() : field.ValueKind == JsonValueKind.Number ? field.GetRawText() : null : null;
    private static JsonElement Data(JsonElement root) => root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
    private static string Required(JsonElement root, string name) => Text(root, name) is { Length: > 0 } text ? text : throw new CloudProviderException("MalformedResponse", $"Baidu omitted the required {name} field.");
    private static string HashHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
