using System.Globalization;
using System.Text.Json;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.Cloud.Baidu;

public sealed partial class BaiduClient
{
    public async Task<CloudAccountInfo> ValidateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        RequireBduss();
        await accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        long generation;
        lock (cacheState)
        {
            if (account is not null && accountValidUntil > options.TimeProvider.GetUtcNow()) return account;
            generation = invalidationGeneration;
        }
        using var document = await JsonAsync(HttpMethod.Get, Url(Pan, "rest/2.0/membership/user/info",
            ("method", "query"), ("clienttype", "0"), ("app_id", "250528"), ("web", "1")),
            null, "account", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("user_info", out var user) || user.ValueKind != JsonValueKind.Object)
            throw new CloudProviderException("MissingAccount", "Baidu did not return a signed-in account.", authenticationRequired: true);
        var id = Num(user, "uk") ?? Num(user, "user_id");
        if (id is null or <= 0)
            throw new CloudProviderException("MissingAccount", "Baidu did not return a valid account identity.", authenticationRequired: true);
        var result = new CloudAccountInfo(id.Value.ToString(CultureInfo.InvariantCulture),
            Text(user, "username") ?? Text(user, "baidu_name") ?? Text(user, "netdisk_name") ?? "百度网盘用户",
            Num(user, "is_svip") == 1 ? 2 : Num(user, "is_vip") == 1 ? 1 : 0,
            Num(root, "total"), Num(root, "used"));
        // Account switching is performed by constructing a new client/session.
        if (account is { } previous && previous.AccountId != result.AccountId)
            throw new CloudProviderException("AccountChanged", "The browser session changed to another Baidu account.", authenticationRequired: true);
        lock (cacheState)
        {
            account = result;
            if (generation == invalidationGeneration) accountValidUntil = options.TimeProvider.GetUtcNow() + options.AccountValidationTtl;
        }
        return result;
        }
        finally { accountGate.Release(); }
    }

    /// <summary>Validate cookies and initialize the web CSRF session before mutations.</summary>
    public async Task InitializeWebSessionAsync(CancellationToken cancellationToken = default)
    {
        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (webToken is not null) return;
            _ = await ValidateAsync(cancellationToken).ConfigureAwait(false);
            using (var warmup = await SendAsync(HttpMethod.Get, new Uri(Pan, "disk/home"), null,
                "session-initialize", cancellationToken).ConfigureAwait(false)) { }
            using var login = await JsonAsync(HttpMethod.Get, Url(Pan, "api/loginStatus",
                ("clienttype", "0"), ("app_id", "250528"), ("web", "1")), null, "session-status", cancellationToken).ConfigureAwait(false);
            using var template = await JsonAsync(HttpMethod.Get, Url(Pan, "api/gettemplatevariable",
                ("clienttype", "0"), ("app_id", "250528"), ("web", "1"), ("fields", "[\"bdstoken\"]")),
                null, "session-token", cancellationToken).ConfigureAwait(false);
            var result = template.RootElement.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.Object ? value : Data(template.RootElement);
            var token = Text(result, "bdstoken");
            if (string.IsNullOrEmpty(token) && login.RootElement.TryGetProperty("login_info", out var info) && info.ValueKind == JsonValueKind.Object)
                token = Text(info, "bdstoken");
            if (string.IsNullOrWhiteSpace(token))
                throw new CloudProviderException("MissingWebToken", "The Baidu web session could not be initialized. Sign in again.", authenticationRequired: true);
            using var userInfo = await JsonAsync(HttpMethod.Get, Url(Pan, "pcloud/user/getinfo",
                ("method", "userinfo"), ("clienttype", "0"), ("app_id", "250528"), ("web", "1"), ("query_uk", account!.AccountId)),
                null, "session-user", cancellationToken).ConfigureAwait(false);
            webToken = token;
        }
        finally { sessionGate.Release(); }
    }

    private string RequireBduss() => cookies.Find("BDUSS", Pan) is { Length: > 0 } bduss ? bduss :
        throw new CloudProviderException("AuthenticationRequired", "Sign in to Baidu in the application's login window.", authenticationRequired: true);

    private Uri WebUrl(string path, params (string Key, string? Value)[] query) => Url(Pan, path,
        query.Concat(new[] { ("clienttype", (string?)"0"), ("app_id", (string?)"250528"), ("web", (string?)"1"), ("bdstoken", webToken) }).ToArray());

    private static Func<HttpContent?> Form(params (string Key, string Value)[] values) =>
        () => new FormUrlEncodedContent(values.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));
}
