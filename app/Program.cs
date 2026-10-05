using System.Diagnostics;
using System.Security.Principal;
using OverlayDisk.Services;
using OverlayDisk.WebHost;
using OverlayDisk.Worker;

namespace OverlayDisk;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool command = args.Length > 0 && args[0] != "--wait-pid";
        try
        {
            if (command)
            {
                var utf8 = new System.Text.UTF8Encoding(false);
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            }
            if (args.Length == 2 && args[0] == "--wait-pid")
            {
                try { using var old = Process.GetProcessById(int.Parse(args[1])); if (!old.WaitForExit(30000)) throw new IOException("原窗口尚未退出。"); }
                catch (ArgumentException) { }
                args = [];
            }
            if (args.Length > 0 && args[0] == "--worker") return WorkerRpcServer.RunAsync(args, new WorkerApplicationService()).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--manual-reclaim-smoke") return ManualReclaimSelfTests.Run(RequirePath(args));
            if (args.Length > 0 && args[0] == "--core-smoke") return SelfTests.RunCore(RequirePath(args));
            if (args.Length > 0 && args[0] == "--object-size-mount-smoke") { ObjectSizeMountSelfTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult(); return 0; }
            if (args.Length > 0 && args[0] == "--activity-smoke") return ActivitySelfTests.Run(RequirePath(args));
            if (args.Length > 0 && args[0] == "--delete-smoke") return DiskDeletionSelfTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--sync-size-smoke")
            {
                string output = RequirePath(args); Directory.CreateDirectory(output);
                foreach (uint size in new uint[] { 4u << 20, 8u << 20, 16u << 20 })
                    OfflineSyncSelfTests.RunAsync(Path.Combine(output, "size-" + (size >> 20)), size).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length > 0 && args[0] == "--sync-offline-smoke") return OfflineSyncSelfTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--cloud-smoke") return LiveCloudSelfTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--restore-smoke") return LocalRestoreSelfTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--lazy-smoke") return LazyHydrationSelfTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--lazy-mount-smoke") return LazyHydrationSelfTests.RunMountAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--restore-mode-smoke") return RestoreModeSelfTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--cache-smoke") return CachePolicySelfTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--readonly-smoke") return ReadOnlySelfTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--readonly-mount-smoke") return ReadOnlySelfTests.RunMountAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--large-readonly-mount-smoke") return ReadOnlySelfTests.RunMountAsync(RequirePath(args), large: true).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--original-mount-smoke") return LazyHydrationSelfTests.RunMountAsync(RequirePath(args), original: true).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--platform-smoke")
            {
                string output = RequirePath(args); Directory.CreateDirectory(output);
                var results = PlatformSelfTests.RunAsync().GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(output, "result.json"), System.Text.Json.JsonSerializer.Serialize(results));
                Console.WriteLine("PLATFORM_SMOKE_OK"); return 0;
            }
            if (args.Length > 0 && args[0] == "--mount-smoke") return SelfTests.RunMountAsync(RequirePath(args)).GetAwaiter().GetResult();
            if (args.Length > 0 && args[0] == "--pipe-server") return SelfTests.RunPipe(RequirePath(args));
            if (args.Length > 0 && args[0] is not ("--ui-smoke" or "--worker-smoke" or "--login-diagnose" or "--login-resume" or "--app-cloud-smoke")) throw new ArgumentException("未知启动参数。");
            // asInvoker does not remove an elevated parent's token. Never instantiate WebView2
            // in that context; use the standard desktop user's token to restart the UI.
            if (UnelevatedLauncher.IsAdministrator) { UnelevatedLauncher.Launch(args); return 0; }
            if (args.Length > 0 && args[0] == "--app-cloud-smoke")
            {
                string testSid = WindowsIdentity.GetCurrent().User!.Value;
                using var testInstance = new Mutex(true, @"Local\OverlayDiskV4-" + testSid, out bool ownsInstance);
                if (!ownsInstance) throw new IOException("请先正常退出 OverlayDisk 界面，再运行应用闭环测试，以免并发改写用户设置。");
                try { return AppCloudSelfTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult(); }
                finally { testInstance.ReleaseMutex(); }
            }
            if (args.Length > 0 && args[0] == "--worker-smoke") return WorkerSmokeTests.RunAsync(RequirePath(args)).GetAwaiter().GetResult();
            ApplicationConfiguration.Initialize();
            if (args.Length > 0 && args[0] == "--login-diagnose") return LoginDiagnostics.Run(RequirePath(args));
            if (args.Length > 0 && args[0] == "--login-resume") return LoginDiagnostics.Run(RequirePath(args), saveSession: true);
            if (args.Length > 0 && args[0] == "--ui-smoke") return WebHostSelfTests.Run(RequirePath(args));
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            string instanceName = @"Local\OverlayDiskV4-" + sid;
            using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "-Activate");
            using var mutex = new Mutex(true, instanceName, out bool first);
            if (!first) { activation.Set(); return 0; }
            var service = new ApplicationService();
            using var form = new MainWebWindow(service);
            var registration = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => form.ShowFromTray(), null, Timeout.Infinite, false);
            try { Application.Run(form); }
            finally { registration.Unregister(null); }
            return 0;
        }
        catch (Exception exception)
        {
            if (command) Console.Error.WriteLine(exception.ToString());
            else
            {
                string logs = Path.Combine(SettingsStorage.DirectoryPath, "Logs");
                Directory.CreateDirectory(logs); File.WriteAllText(Path.Combine(logs, "startup-error.txt"), exception.ToString());
            }
            return 1;
        }
    }
    private static string RequirePath(string[] args) => args.Length == 2 ? Path.GetFullPath(args[1]) : throw new ArgumentException("请提供新的测试输出目录。");
}
