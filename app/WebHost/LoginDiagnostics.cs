using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using OverlayDisk.Cloud.Baidu;
using OverlayDisk.Cloud.Contracts;

namespace OverlayDisk.WebHost;

internal static class LoginDiagnostics
{
    private sealed class DiagnosticWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08000000; return parameters; }
        }
    }
    internal static void Record(string stage, IEnumerable<string>? cookieNames = null, Exception? error = null)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverlayDisk", "v4", "Logs");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "login-diagnostic.json"), JsonSerializer.Serialize(new
            {
                timeUtc = DateTimeOffset.UtcNow, stage,
                cookieNames = cookieNames?.Distinct().Order().ToArray(),
                code = error is CloudProviderException cloud ? cloud.Code : error?.GetType().Name,
                authenticationRequired = error is CloudProviderException { AuthenticationRequired: true }
            }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static int Run(string output, bool saveSession = false)
    {
        Directory.CreateDirectory(output);
        int exitCode = 1;
        var events = new List<BaiduDiagnostic>();
        string stage = "browser-profile";
        string[] names = [];
        using var window = new DiagnosticWindow { ShowInTaskbar = false, Opacity = 0, Width = 1, Height = 1, FormBorderStyle = FormBorderStyle.None };
        using var browser = new WebView2 { Dock = DockStyle.Fill };
        window.Controls.Add(browser);
        window.Shown += async (_, _) =>
        {
            try
            {
                string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverlayDisk", "v4", "WebProfiles", "BaiduLogin");
                var environment = await CoreWebView2Environment.CreateAsync(null, profile);
                await browser.EnsureCoreWebView2Async(environment);
                var core = browser.CoreWebView2;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.NavigationStarting += (_, e) => e.Cancel = true;
                core.NewWindowRequested += (_, e) => e.Handled = true;
                stage = "cookie-read";
                var cookies = await core.CookieManager.GetCookiesAsync("https://pan.baidu.com/");
                names = cookies.Select(c => c.Name).Distinct().Order().ToArray();
                var records = cookies.Select(c => new BaiduCookieRecord(c.Name, c.Value, c.Domain, c.Path, c.IsSecure, c.IsHttpOnly,
                    c.IsSession ? null : new DateTimeOffset(c.Expires.ToUniversalTime()))).ToArray();
                var session = new BaiduCookieSession(records, core.Settings.UserAgent);
                await using var client = new BaiduClient(session, options: new BaiduClientOptions { Diagnostic = entry => { lock (events) events.Add(entry); } });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                stage = "account-validate";
                await client.ValidateAsync(timeout.Token);
                stage = "web-session-initialize";
                await client.InitializeWebSessionAsync(timeout.Token);
                if (saveSession)
                {
                    stage = "save-protected-session";
                    string sessionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OverlayDisk", "v4", "baidu-session.dpapi");
                    await WindowsSessionVault.SaveAsync(sessionPath, client.ExportSession(), timeout.Token);
                }
                stage = "ready"; exitCode = 0;
                Record(stage, names);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { ok = true, stage, saved = saveSession, cookieNames = names, diagnostics = events }));
            }
            catch (Exception error)
            {
                Record(stage, names, error);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    ok = false, stage, cookieNames = names,
                    code = error is CloudProviderException provider ? provider.Code : error.GetType().Name,
                    authenticationRequired = error is CloudProviderException { AuthenticationRequired: true }, diagnostics = events
                }));
            }
            finally { window.Close(); }
        };
        Application.Run(window);
        Console.WriteLine(exitCode == 0 ? "LOGIN_DIAGNOSE_READY" : "LOGIN_DIAGNOSE_FAILED");
        return exitCode;
    }
}
