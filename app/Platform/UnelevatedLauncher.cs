using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OverlayDisk;

internal static class UnelevatedLauncher
{
    internal static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    internal static void Launch(string[] args)
    {
        IntPtr shell = GetShellWindow();
        if (shell == IntPtr.Zero) throw new InvalidOperationException("请从 Windows 桌面以普通用户权限打开 OverlayDisk。");
        GetWindowThreadProcessId(shell, out uint processId);
        using var process = OpenProcess(0x1000, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, 0x0002 | 0x0008, out var token)) throw new Win32Exception();
        using (token)
        {
            if (!GetTokenInformation(token, 20, out int elevated, sizeof(int), out _) || elevated != 0)
                throw new InvalidOperationException("Windows 桌面进程权限异常，无法安全打开网页界面。");
            if (!DuplicateTokenEx(token, 0x000F01FF, IntPtr.Zero, 2, 1, out var primary)) throw new Win32Exception();
            using (primary)
            {
                string executable = Environment.ProcessPath!;
                var arguments = new List<string> { executable };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) arguments.Add(Assembly.GetExecutingAssembly().Location);
                arguments.AddRange(args);
                var command = new StringBuilder(string.Join(" ", arguments.Select(Quote)));
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
                if (!CreateProcessWithTokenW(primary, 0, executable, command, 0x00000400, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out var created)) throw new Win32Exception();
                CloseHandle(created.Process); CloseHandle(created.Thread);
            }
        }
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
            else { result.Append('\\', slashes); result.Append(c); }
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Size; internal string? Reserved; internal string? Desktop; internal string? Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedSize; internal IntPtr ReservedBytes, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, out int value, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int impersonation, int tokenType, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, uint logonFlags, string application, StringBuilder command, uint creationFlags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
