using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace HeadStartVR.Services;

/// <summary>
/// The launcher runs elevated so the UEVR injector can start as administrator without a UAC prompt
/// appearing while the headset is on. Everything else (game, Steam, OFXR tray, VD Streamer) is started
/// with the desktop shell's normal-user token, as if it had been double-clicked.
/// </summary>
public static class ProcessLauncher
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Start a program as the normal (unelevated) desktop user. Returns its process id.</summary>
    public static int StartAsUser(string file, string arguments = "", string? workingDir = null)
    {
        workingDir ??= System.IO.Path.GetDirectoryName(file);
        if (IsElevated)
        {
            try
            {
                return StartWithShellToken(file, arguments, workingDir);
            }
            catch (Exception ex)
            {
                Log.Write($"Unelevated start of {file} failed ({ex.Message}); starting it elevated instead.");
            }
        }

        var psi = new ProcessStartInfo(file, arguments) { UseShellExecute = false, WorkingDirectory = workingDir ?? "" };
        using var p = Process.Start(psi)!;
        return p.Id;
    }

    /// <summary>Open a URI such as steam://rungameid/123 as the normal desktop user.</summary>
    public static void OpenUriAsUser(string uri)
    {
        var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (IsElevated)
        {
            StartAsUser(explorer, $"\"{uri}\"");
            return;
        }
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    /// <summary>Start a program as administrator (directly when we are elevated, otherwise via a UAC prompt).</summary>
    public static Process StartElevated(string file, IEnumerable<string> args, string workingDir)
    {
        if (IsElevated)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = workingDir };
            foreach (var a in args) psi.ArgumentList.Add(a);
            return Process.Start(psi)!;
        }

        var shellPsi = new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = workingDir,
            Arguments = string.Join(' ', args.Select(Quote)),
        };
        return Process.Start(shellPsi)!;
    }

    private static string Quote(string a) => a.Contains(' ') || a.Contains('"') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

    private static int StartWithShellToken(string file, string arguments, string? workingDir)
    {
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero) throw new InvalidOperationException("No desktop shell window found.");
        GetWindowThreadProcessId(shell, out var shellPid);

        var hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
        if (hProc == IntPtr.Zero) throw new Win32Exception();
        IntPtr hToken = IntPtr.Zero, hPrimary = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_DUPLICATE, out hToken)) throw new Win32Exception();
            const uint access = TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            if (!DuplicateTokenEx(hToken, access, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out hPrimary))
                throw new Win32Exception();

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var cmd = new StringBuilder($"\"{file}\" {arguments}".TrimEnd(), 32768);
            if (!CreateProcessWithTokenW(hPrimary, 0, file, cmd, 0, IntPtr.Zero, workingDir, ref si, out var pi))
                throw new Win32Exception();
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally
        {
            if (hPrimary != IntPtr.Zero) CloseHandle(hPrimary);
            if (hToken != IntPtr.Zero) CloseHandle(hToken);
            CloseHandle(hProc);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string? appName, StringBuilder cmdLine,
        int creationFlags, IntPtr env, string? currentDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
}
