using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using OverlayDisk.Worker;

namespace OverlayDisk.WebHost;

internal static class WebHostSelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        Exception? failure = null;
        string? runtime = null;
        var checks = new List<string>();
        using var window = new MainWebWindow(new SmokeService(), Path.Combine(output, "browser-profile"));
        window.Shown += async (_, _) =>
        {
            try
            {
                if (UnelevatedLauncher.IsAdministrator) throw new InvalidOperationException("Web UI is elevated.");
                await window.Ready.WaitAsync(TimeSpan.FromSeconds(35));
                await Task.Delay(1500);
                if (window.Browser is null || !MainWebWindow.IsTrusted(window.Browser.Source)) throw new InvalidOperationException("Trusted local UI did not load.");
                checks.Add("standard-user WebView2 loaded trusted local UI");
                string dom = await window.Browser.ExecuteScriptAsync("JSON.stringify({title:document.title,body:document.body.innerText,ready:document.readyState,width:innerWidth,height:innerHeight,ratio:devicePixelRatio,scrollWidth:document.body.scrollWidth})");
                File.WriteAllText(Path.Combine(output, "page.json"), dom);
                if (!dom.Contains("OverlayDisk", StringComparison.Ordinal)) throw new InvalidOperationException("Product page was not rendered.");
                if (await window.Browser.ExecuteScriptAsync("document.body.scrollWidth <= innerWidth") != "true") throw new InvalidOperationException("Web layout is clipped at the active desktop DPI.");
                using (var capture = File.Create(Path.Combine(output, "main-window.png")))
                    await window.Browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
                if (window.Browser.Settings.AreHostObjectsAllowed || !window.Browser.Settings.IsWebMessageEnabled) throw new InvalidOperationException("Trusted page bridge settings invalid.");
                if (MainWebWindow.IsTrusted("https://overlaydisk.local.evil.test/") || MainWebWindow.IsTrusted("http://overlaydisk.local/") || MainWebWindow.IsTrusted("https://overlaydisk.local:444/") || MainWebWindow.IsTrusted("https://x@overlaydisk.local/")) throw new InvalidOperationException("Origin guard accepted an untrusted URL.");
                if (MainWebWindow.IsBaiduLoginUri("https://baidu.com.evil.test/") || MainWebWindow.IsBaiduLoginUri("file:///C:/")) throw new InvalidOperationException("Login origin guard accepted an untrusted URL.");
                checks.Add("origin guards and host-object isolation");
                await window.Browser.ExecuteScriptAsync("window.__bridgeSmoke=null;window.chrome.webview.addEventListener('message',e=>{if(e.data.requestId==='smoke-echo')window.__bridgeSmoke=e.data;});window.chrome.webview.postMessage({requestId:'smoke-echo',method:'test.echo',args:{value:42}});");
                for (int i = 0; i < 40; i++)
                {
                    if (await window.Browser.ExecuteScriptAsync("Boolean(window.__bridgeSmoke?.ok && window.__bridgeSmoke.data.value===42)") == "true") break;
                    await Task.Delay(50);
                }
                if (await window.Browser.ExecuteScriptAsync("Boolean(window.__bridgeSmoke?.ok && window.__bridgeSmoke.data.value===42)") != "true") throw new IOException("Web message round trip failed.");
                window.Browser.Navigate("https://example.invalid/");
                await Task.Delay(200);
                if (!MainWebWindow.IsTrusted(window.Browser.Source)) throw new IOException("Remote navigation was not blocked.");
                checks.Add("real page RPC round trip and remote navigation rejection");
                var login = await window.PrepareLoginForSmokeAsync();
                if (login.Settings.IsWebMessageEnabled || login.Settings.AreHostObjectsAllowed || login.Settings.IsPasswordAutosaveEnabled
                    || string.Equals(login.Environment.UserDataFolder, window.Browser.Environment.UserDataFolder, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Official login view is not isolated from the trusted application view.");
                checks.Add("separate login browser profile with host bridge and password saving disabled");
                await window.Browser.ExecuteScriptAsync("window.__exitBlocked=false;window.chrome.webview.addEventListener('message',e=>{if(e.data.type==='exitBlocked')window.__exitBlocked=true;});window.chrome.webview.postMessage({requestId:'smoke-exit',method:'host.exit',args:{}});");
                for (int i = 0; i < 40 && await window.Browser.ExecuteScriptAsync("window.__exitBlocked") != "true"; i++) await Task.Delay(50);
                if (await window.Browser.ExecuteScriptAsync("window.__exitBlocked") != "true" || window.IsDisposed || !window.Visible)
                    throw new IOException("Pending synchronization did not block exit.");
                using (var capture = File.Create(Path.Combine(output, "exit-blocked.png")))
                    await window.Browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
                await window.Browser.ExecuteScriptAsync("document.querySelector('dialog')?.close()");
                checks.Add("exit awaits service and displays web prompt when synchronization is pending");
                bool denied = false;
                try { WorkerProtocol.ValidateMethod("system.execute"); } catch (InvalidOperationException) { denied = true; }
                if (!denied) throw new InvalidOperationException("Worker method allowlist missing.");
                checks.Add("worker RPC rejects arbitrary commands");
                window.Close();
                if (window.IsDisposed || window.Visible) throw new InvalidOperationException("Window close did not hide to tray.");
                window.ShowFromTray();
                checks.Add("close hides to tray and activation restores window");
                runtime = window.Browser.Environment.BrowserVersionString;
            }
            catch (Exception error) { failure = error; File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString(), checks })); }
            finally { window.CloseForSmokeTest(); }
        };
        Application.Run(window);
        window.Dispose();
        if (failure is not null) throw failure;
        Console.WriteLine("UI_SMOKE_OK");
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, elevated = false, processId = Environment.ProcessId, runtime, checks }));
        return 0;
    }
    private sealed class SmokeService : IApplicationService
    {
        public event EventHandler? StateChanged { add { } remove { } }
        public Task<object?> GetStateAsync(CancellationToken ct) => Task.FromResult<object?>(new
        {
            version = "0.4.0", disks = Array.Empty<object>(), workerConnected = false,
            account = (object?)null, cloud = new { loggedIn = false }, tasks = Array.Empty<object>(),
            driver = new { installed = true, running = true }, settings = new { uploadConcurrency = 3, downloadConcurrency = 3 }
        });
        public Task<object?> InvokeAsync(string method, JsonElement args, CancellationToken ct)
            => method is "state" or "app.state" ? GetStateAsync(ct) : Task.FromResult<object?>(method == "test.echo" ? args.Clone() : null);
        public Task ShutdownAsync(CancellationToken ct) => Task.FromException(new IOException("还有内容等待上传；本地数据已保存。"));
    }
}
