using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OverlayDisk.WebHost;

public sealed class MainWebWindow : Form
{
    private const string TrustedOrigin = "https://overlaydisk.local";
    private readonly IApplicationService _service;
    private readonly WebView2 _main = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(247, 249, 252) };
    private readonly WebView2 _loginView = new() { Visible = false, DefaultBackgroundColor = Color.White };
    private readonly NotifyIcon _tray;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Windows.Forms.Timer _loginPoll = new() { Interval = 1500 };
    private readonly string _profileRoot;
    private bool _closing;
    private bool _resourcesDisposed;
    private bool _exitPending;
    private bool _statePublishing;
    private bool _stateDirty;
    private bool _checkingLogin;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private CancellationTokenSource? _loginCancellation;
    private TaskCompletionSource<object?>? _loginCompletion;
    private string? _lastCookieFingerprint;
    private DateTimeOffset _nextLoginAttempt;
    internal Task Ready => _ready.Task;
    internal CoreWebView2? Browser => _main.CoreWebView2;
    internal async Task<CoreWebView2> PrepareLoginForSmokeAsync()
    {
        await EnsureLoginCoreAsync();
        return _loginView.CoreWebView2!;
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MainWebWindow(IApplicationService service, string? isolatedProfileRoot = null)
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("OverlayDisk 主界面必须以普通用户权限启动。");
        _service = service;
        _profileRoot = isolatedProfileRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverlayDisk", "v4", "WebProfiles");
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "OverlayDisk";
        MinimumSize = new Size(1040, 720);
        Size = new Size(1320, 860);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 249, 252);
        string iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "icon.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        Controls.Add(_main);
        Controls.Add(_loginView);
        _loginView.BringToFront();
        Layout += (_, _) =>
        {
            float scale = DeviceDpi / 96f;
            _loginView.Bounds = new Rectangle((int)(24 * scale), (int)(104 * scale), Math.Max(1, ClientSize.Width - (int)(48 * scale)), Math.Max(1, ClientSize.Height - (int)(128 * scale)));
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 OverlayDisk", null, (_, _) => ShowFromTray());
        menu.Items.Add("退出", null, async (_, _) => await RequestExitAsync());
        _tray = new NotifyIcon { Icon = Icon, Text = "OverlayDisk", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _loginPoll.Tick += async (_, _) => await CheckLoginAsync();
        _service.StateChanged += OnStateChanged;
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, e) => { if (!_closing) { e.Cancel = true; Hide(); } };
    }

    public void ShowFromTray()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(ShowFromTray); } catch (InvalidOperationException) { }
            return;
        }
        Show(); WindowState = FormWindowState.Normal; Activate();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // This window is built in code (no designer autoscale pass). Size it in logical
        // pixels so 150%/175% desktop scaling does not clip the web page's toolbar.
        float scale = DeviceDpi / 96f;
        Rectangle work = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min((int)(980 * scale), work.Width - 32), Math.Min((int)(640 * scale), work.Height - 32));
        Size = new Size(Math.Min((int)(1320 * scale), work.Width - 48), Math.Min((int)(860 * scale), work.Height - 48));
        Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_profileRoot);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_profileRoot, "Interface"));
            await _main.EnsureCoreWebView2Async(environment);
            var core = _main.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.SetVirtualHostNameToFolderMapping("overlaydisk.local", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => { if (!IsTrusted(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                // Only the fixed attribution link may open outside the isolated UI.
                if (e.IsUserInitiated && e.Uri == "https://github.com/winfsp/winspd")
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                    catch (Exception ex) { MessageBox.Show(this, "无法打开浏览器：" + ex.Message, "打开链接", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                }
            };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !IsTrusted(uri.AbsoluteUri))
                    e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Forbidden", "Content-Type: text/plain");
            };
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) { _ready.TrySetException(new IOException("应用页面加载失败：" + e.WebErrorStatus)); return; }
                _ready.TrySetResult(true);
                await PublishStateAsync();
            };
            core.Navigate(TrustedOrigin + "/index.html");
        }
        catch (Exception e)
        {
            _ready.TrySetException(e);
            // A missing/broken WebView runtime cannot render the product UI. Keep a diagnostic
            // file, never fall back to a second privileged or WinForms product interface.
            Directory.CreateDirectory(_profileRoot);
            File.WriteAllText(Path.Combine(_profileRoot, "startup-error.txt"), e.ToString());
            _closing = true; Close();
        }
    }

    internal static bool IsTrusted(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.Equals("overlaydisk.local", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsTrusted(e.Source)) return;
        object? requestId = null;
        try
        {
            string json = e.WebMessageAsJson;
            if (json.Length > 1024 * 1024) throw new InvalidDataException("界面请求过大。");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
            var request = document.RootElement;
            var id = request.GetProperty("requestId");
            if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) throw new InvalidDataException("界面请求标识无效。");
            requestId = id.Clone();
            string method = request.GetProperty("method").GetString() ?? "";
            JsonElement args = request.TryGetProperty("args", out var value) ? value.Clone() : default;
            // Return to the message loop before native file pickers: WebView2 disallows modal
            // reentrancy while its WebMessageReceived callback remains on the stack.
            await Task.Yield();
            if (_exitPending && method is not ("host.exit" or "app.state" or "host.cancelLogin"))
                throw new InvalidOperationException("正在保存并退出，请稍候。");
            object? result = method switch
            {
                "host.pickContainer" => PickContainer(args),
                "host.login" => await LoginAsync(),
                "host.logout" or "auth.logout" => await LogoutAsync(),
                "host.cancelLogin" => CancelLogin(),
                "host.exit" => await RequestExitAsync(),
                _ when method.StartsWith("host.", StringComparison.Ordinal) => throw new InvalidOperationException("未知界面操作。"),
                _ => await _service.InvokeAsync(method, args, _lifetime.Token)
            };
            Post(new { requestId, ok = true, data = result });
        }
        catch (Exception error) { Post(new { requestId, ok = false, error = error.Message }); }
    }

    private string? PickContainer(JsonElement args)
    {
        bool save = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("save", out var value) && value.ValueKind == JsonValueKind.True;
        string suggested = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("suggestedName", out var name) ? Path.GetFileName(name.GetString()) ?? "新磁盘.odv4" : "新磁盘.odv4";
        using FileDialog dialog = save
            ? new SaveFileDialog { FileName = suggested, OverwritePrompt = true, AddExtension = true, DefaultExt = "odv4" }
            : new OpenFileDialog { CheckFileExists = true, Multiselect = false };
        dialog.Filter = "OverlayDisk 磁盘 (*.odv4)|*.odv4|所有文件 (*.*)|*.*";
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private async Task<object?> LoginAsync()
    {
        if (_loginCompletion is not null) return await _loginCompletion.Task;
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loginCompletion = completion;
        _loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _lastCookieFingerprint = null;
        _nextLoginAttempt = DateTimeOffset.MinValue;
        try
        {
            await EnsureLoginCoreAsync();
            if (_loginCompletion != completion) return await completion.Task;
            _loginView.Visible = true; _loginView.BringToFront();
            Post(new { type = "loginVisibility", visible = true });
            _loginView.CoreWebView2!.Navigate("https://pan.baidu.com/login");
            _loginPoll.Start();
            return await completion.Task;
        }
        finally
        {
            if (_loginCompletion == completion)
            {
                _loginCompletion = null;
                _loginCancellation?.Cancel();
                _loginCancellation?.Dispose();
                _loginCancellation = null;
            }
            _loginPoll.Stop(); _loginView.Visible = false;
            Post(new { type = "loginVisibility", visible = false });
        }
    }

    private async Task EnsureLoginCoreAsync()
    {
            if (_loginView.CoreWebView2 is null)
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_profileRoot, "BaiduLogin"));
                await _loginView.EnsureCoreWebView2Async(environment);
                var core = _loginView.CoreWebView2!;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.NavigationStarting += (_, e) => { if (!IsBaiduLoginUri(e.Uri)) e.Cancel = true; };
                core.NewWindowRequested += (_, e) => { e.Handled = true; if (IsBaiduLoginUri(e.Uri)) core.Navigate(e.Uri); };
            }
    }

    internal static bool IsBaiduLoginUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host.Equals("baidu.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".baidu.com", StringComparison.OrdinalIgnoreCase));

    private async Task CheckLoginAsync()
    {
        if (_checkingLogin || _loginCompletion is null || _loginView.CoreWebView2 is null || DateTimeOffset.UtcNow < _nextLoginAttempt) return;
        _checkingLogin = true;
        var completion = _loginCompletion;
        CancellationToken cancellation = _loginCancellation?.Token ?? _lifetime.Token;
        string[] cookieNames = [];
        await _authGate.WaitAsync();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // Only this application's isolated Baidu profile is queried, and only cookies
            // applicable to pan.baidu.com. COM properties are copied on the WebView UI thread.
            var cookies = await _loginView.CoreWebView2.CookieManager.GetCookiesAsync("https://pan.baidu.com/");
            cookieNames = cookies.Select(cookie => cookie.Name).Distinct().Order().ToArray();
            LoginDiagnostics.Record("cookie-read", cookieNames);
            var records = cookies.Select(cookie => new
            {
                name = cookie.Name, value = cookie.Value, domain = cookie.Domain, path = cookie.Path,
                secure = cookie.IsSecure, httpOnly = cookie.IsHttpOnly,
                expiresUtc = cookie.IsSession ? (DateTimeOffset?)null : new DateTimeOffset(cookie.Expires.ToUniversalTime())
            }).ToArray();
            if (!records.Any(cookie => cookie.name == "BDUSS" && !string.IsNullOrEmpty(cookie.value))) return;
            byte[] cookieBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                cookies = records,
                userAgent = _loginView.CoreWebView2.Settings.UserAgent,
                formatVersion = 1
            }, JsonOptions);
            try
            {
                string fingerprint = Convert.ToHexString(SHA256.HashData(cookieBytes));
                if (fingerprint == _lastCookieFingerprint) return;
                _lastCookieFingerprint = fingerprint;
                using var document = JsonDocument.Parse(cookieBytes);
                cancellation.ThrowIfCancellationRequested();
                LoginDiagnostics.Record("accept-cookies", cookieNames);
                object? result = await _service.InvokeAsync("auth.acceptCookies", document.RootElement, cancellation);
                LoginDiagnostics.Record("ready", cookieNames);
                if (_loginCompletion == completion) completion.TrySetResult(result);
            }
            finally { CryptographicOperations.ZeroMemory(cookieBytes); }
        }
        catch (OperationCanceledException) { completion.TrySetCanceled(); }
        catch (Exception error)
        {
            LoginDiagnostics.Record("accept-cookies-failed", cookieNames, error);
            // A cookie is only a candidate. The backend validates account status before login
            // completes; network/challenge failures leave the official page available.
            if (_loginCompletion == completion)
            {
                _lastCookieFingerprint = null;
                _nextLoginAttempt = DateTimeOffset.UtcNow.AddSeconds(10);
                Post(new { type = "loginStatus", message = error.Message });
            }
        }
        finally { _checkingLogin = false; _authGate.Release(); }
    }

    private object? CancelLogin()
    {
        _loginCancellation?.Cancel();
        _loginCompletion?.TrySetCanceled();
        _loginPoll.Stop(); _loginView.Visible = false;
        return null;
    }

    private async Task ClearLoginProfileAsync()
    {
        await EnsureLoginCoreAsync();
        await _loginView.CoreWebView2!.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
    }

    private async Task<object?> LogoutAsync()
    {
        CancelLogin();
        await _authGate.WaitAsync(_lifetime.Token);
        try
        {
            await ClearLoginProfileAsync();
            return await _service.InvokeAsync("auth.logout", JsonSerializer.SerializeToElement(new { }), _lifetime.Token);
        }
        finally { _authGate.Release(); }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(async () => await PublishStateAsync()); } catch (InvalidOperationException) { }
    }

    private async Task PublishStateAsync()
    {
        if (!_ready.Task.IsCompletedSuccessfully || _closing) return;
        _stateDirty = true;
        if (_statePublishing) return;
        _statePublishing = true;
        try
        {
            while (_stateDirty && !_closing)
            {
                _stateDirty = false;
                Post(new { type = "state", data = await _service.GetStateAsync(_lifetime.Token) });
            }
        }
        catch (Exception error) { Post(new { type = "error", message = error.Message }); }
        finally { _statePublishing = false; }
    }

    private void Post(object message)
    {
        if (_closing || IsDisposed || _main.CoreWebView2 is null || !IsTrusted(_main.CoreWebView2.Source)) return;
        _main.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private async Task<bool> RequestExitAsync()
    {
        if (_exitPending) return false;
        _exitPending = true;
        try
        {
            CancelLogin();
            await _authGate.WaitAsync(_lifetime.Token);
            try { await _service.ShutdownAsync(_lifetime.Token); }
            finally { _authGate.Release(); }
            _closing = true; _tray.Visible = false; Close();
            return true;
        }
        catch (Exception error)
        {
            ShowFromTray();
            Post(new { type = "exitBlocked", message = error.Message });
            return false;
        }
        finally { _exitPending = false; }
    }

    internal void CloseForSmokeTest() { _closing = true; Close(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _service.StateChanged -= OnStateChanged;
            _lifetime.Cancel(); _loginCompletion?.TrySetCanceled();
            _loginPoll.Dispose(); _tray.Dispose(); _loginView.Dispose(); _main.Dispose(); _lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
