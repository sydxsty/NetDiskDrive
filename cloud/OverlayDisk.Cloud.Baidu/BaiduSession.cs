using System.Net;

namespace OverlayDisk.Cloud.Baidu;

/// <summary>Export these fields from the application's own WebView2 cookie manager.</summary>
public sealed record BaiduCookieRecord(
    string Name,
    string Value,
    string Domain,
    string Path = "/",
    bool Secure = true,
    bool HttpOnly = true,
    DateTimeOffset? ExpiresUtc = null)
{
    public override string ToString() => "BaiduCookieRecord { Value = <redacted> }";
}

public sealed record BaiduCookieSession(
    IReadOnlyList<BaiduCookieRecord> Cookies,
    string? UserAgent = null,
    int FormatVersion = 1)
{
    public override string ToString() => "BaiduCookieSession { Cookies = <redacted> }";
}

public sealed class BaiduClientOptions
{
    /// <summary>All clients share the global process budget by default, including complete download bodies.</summary>
    public BaiduRequestScheduler RequestScheduler { get; init; } = BaiduRequestScheduler.Shared;
    /// <summary>Enable local metadata proofs only when this application is the sole cloud writer.</summary>
    public bool AssumeExclusiveWriter { get; init; }
    /// <summary>Optional per-account persistent complete directory indexes; contains metadata, never cookies.</summary>
    public string? MetadataCacheDirectory { get; init; }
    public int MaximumCachedMetadataEntries { get; init; } = 8192;
    public TimeSpan AccountValidationTtl { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan UploadEndpointTtl { get; init; } = TimeSpan.FromMinutes(5);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public int MaximumAttempts { get; init; } = 3;
    public int PageSize { get; init; } = 1000;
    public int MaximumListPages { get; init; } = 100_000;
    public int MaximumTaskPolls { get; init; } = 60;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan TaskPollDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public long MaximumUploadLength { get; init; } = 4L * 1024 * 1024 * 1024;
    /// <summary>Receives operation labels and error codes only; never URLs, cookies or response bodies.</summary>
    public Action<BaiduDiagnostic>? Diagnostic { get; init; }
}

public sealed record BaiduDiagnostic(string Operation, string Code, int? HttpStatus = null);

internal sealed class BaiduCookieJar
{
    // WebView2 sessions can exceed CookieContainer's default 20 cookies/domain;
    // silently evicting BDUSS during import would turn a valid login into failure.
    private readonly CookieContainer jar = new(2048, 512, 16 * 1024);
    private readonly object gate = new();

    public BaiduCookieJar(BaiduCookieSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(session.Cookies);
        if (session.Cookies.Count > 2048 || session.Cookies.GroupBy(c => c?.Domain?.TrimStart('.') ?? "", StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 512))
            throw new ArgumentException("The browser session contains too many cookies.", nameof(session));
        if (session.FormatVersion != 1) throw new ArgumentException("Unsupported cookie session version.", nameof(session));
        foreach (var source in session.Cookies)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Domain))
                throw new ArgumentException("Cookie domain is missing.", nameof(session));
            var domain = source.Domain.TrimStart('.').ToLowerInvariant();
            if (domain != "baidu.com" && !domain.EndsWith(".baidu.com", StringComparison.Ordinal))
                throw new ArgumentException("Only Baidu-domain cookies can be imported.", nameof(session));
            try
            {
                var cookie = new Cookie(source.Name, source.Value, string.IsNullOrEmpty(source.Path) ? "/" : source.Path, source.Domain)
                {
                    Secure = source.Secure,
                    HttpOnly = source.HttpOnly
                };
                if (source.ExpiresUtc is { } expires) cookie.Expires = expires.UtcDateTime;
                jar.Add(cookie);
            }
            catch (Exception error) when (error is CookieException or ArgumentException)
            { throw new ArgumentException("The browser session contains an invalid cookie.", nameof(session)); }
        }
    }

    public string Header(Uri uri)
    {
        lock (gate)
        {
            // Prefer the most specific domain/path when pan and passport have
            // issued different values under the same cookie name.
            return string.Join("; ", jar.GetCookies(uri).Cast<Cookie>()
                .OrderByDescending(c => c.Domain.TrimStart('.').Length)
                .ThenByDescending(c => c.Path.Length)
                .DistinctBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => $"{c.Name}={c.Value}"));
        }
    }

    public string? Find(string name, Uri uri)
    {
        lock (gate)
        {
            return jar.GetCookies(uri).Cast<Cookie>()
                .Where(c => c.Name == name)
                .OrderByDescending(c => c.Domain.TrimStart('.').Length)
                .ThenByDescending(c => c.Path.Length)
                .Select(c => c.Value).FirstOrDefault();
        }
    }

    public void Observe(Uri uri, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        lock (gate)
        {
            foreach (var value in values)
            {
                try { jar.SetCookies(uri, value); }
                catch (CookieException) { /* Ignore malformed optional cookies; never log credentials. */ }
            }
        }
    }

    public BaiduCookieSession Export(string userAgent)
    {
        lock (gate)
        {
            return new BaiduCookieSession(jar.GetAllCookies().Cast<Cookie>()
                .Where(c => !c.Expired)
                .Select(c => new BaiduCookieRecord(c.Name, c.Value, c.Domain, c.Path, c.Secure, c.HttpOnly,
                    c.Expires == DateTime.MinValue ? null : new DateTimeOffset(c.Expires.ToUniversalTime())))
                .ToArray(), userAgent);
        }
    }
}
