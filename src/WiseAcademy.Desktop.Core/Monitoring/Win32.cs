using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WiseAcademy.Desktop.Core.Monitoring;

/// <summary>Chamadas Win32 usadas pelo agente (janela em foco, inatividade, hooks de teclado/rato).</summary>
internal static class Win32
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Segundos desde a última tecla/movimento (de qualquer aplicação).</summary>
    public static int IdleSeconds()
    {
        var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref i)) return 0;
        return (int)(((uint)Environment.TickCount - i.dwTime) / 1000);
    }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const uint WM_QUIT = 0x0012;
}

/// <summary>Janela em primeiro plano: aplicação, título e (nos navegadores) o URL.</summary>
public sealed record ForegroundInfo(IntPtr Handle, string ProcessName, string AppName, string Title, string? Url)
{
    public static readonly ForegroundInfo None = new(IntPtr.Zero, "", "", "", null);
    public string Key => $"{ProcessName}|{Title}|{Url}";
}

internal static class Foreground
{
    private static readonly string[] Browsers = ["chrome", "msedge", "brave", "opera", "vivaldi", "firefox"];
    private static (IntPtr h, string title, string? url) _urlCache;

    public static ForegroundInfo Read(bool withUrl)
    {
        try
        {
            var h = Win32.GetForegroundWindow();
            if (h == IntPtr.Zero) return ForegroundInfo.None;
            var sb = new StringBuilder(512);
            Win32.GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            Win32.GetWindowThreadProcessId(h, out var pid);
            using var p = Process.GetProcessById((int)pid);
            var proc = p.ProcessName;
            string app = proc;
            try { app = p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : proc; } catch { /* processo protegido */ }
            string? url = null;
            if (withUrl && Browsers.Contains(proc.ToLowerInvariant()))
            {
                // só volta a perguntar ao UI Automation quando o separador (título) muda
                if (_urlCache.h == h && _urlCache.title == title) url = _urlCache.url;
                else { url = BrowserUrl(h); _urlCache = (h, title, url); }
            }
            return new ForegroundInfo(h, proc, app, title, url);
        }
        catch { return ForegroundInfo.None; }
    }

    /// <summary>Lê a barra de endereço via UI Automation (porte do TimeZoon `BrowserUrlReader`).</summary>
    private static string? BrowserUrl(IntPtr h)
    {
        try
        {
            var win = AutomationElement.FromHandle(h);
            if (win is null) return null;
            var edits = win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            foreach (AutomationElement el in edits)
            {
                if (!el.TryGetCurrentPattern(ValuePattern.Pattern, out var pat)) continue;
                var v = ((ValuePattern)pat).Current.Value?.Trim();
                if (string.IsNullOrEmpty(v) || v.Contains(' ')) continue;
                var c = v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? v : "https://" + v;
                if (Uri.TryCreate(c, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.Host.Contains('.'))
                    return u.AbsoluteUri;
            }
        }
        catch { /* janela fechou ou UIA indisponível */ }
        return null;
    }
}
