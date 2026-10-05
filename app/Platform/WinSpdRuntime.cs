using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace OverlayDisk;

public sealed record DriverStatus(bool IsAdministrator, bool NativeLibraryAvailable,
    bool DriverInstalled, string? NativeLibraryPath, string Message);

public static class WinSpdRuntime
{
    public static bool IsAdministrator => OperatingSystem.IsWindows() &&
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static string? FindLibrary(string fileName = "winspd-x64.dll")
    {
        if (Path.GetFileName(fileName) != fileName) throw new ArgumentException("Invalid library name.");
        var local = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(local)) return local;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(@"SOFTWARE\WinSpd");
            if (key?.GetValue("InstallDir") is not string directory) continue;
            var candidate = Path.Combine(directory, "bin", fileName);
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static IntPtr LoadLibrary(string fileName)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("OverlayDisk requires x64.");
        var path = FindLibrary(fileName) ?? throw new DllNotFoundException(
            "找不到 WinSpd 本机库。请使用完整发布包，或安装官方 WinSpd。");
        return NativeLibrary.Load(path);
    }

    public static DriverStatus GetDriverStatus()
    {
        string? native = FindLibrary();
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WinSpd");
        bool installed = service is not null;
        string message = !installed ? "未检测到 WinSpd 驱动；仍可运行核心与命名管道验证。"
            : "检测到 WinSpd 安装记录；实际驱动加载及当前 Windows 安全设置兼容性需挂载验证。";
        return new(IsAdministrator, native is not null, installed, native, message);
    }
}
