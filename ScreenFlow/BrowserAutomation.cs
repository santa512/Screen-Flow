using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenFlow;
internal sealed record BrowserWindow(IntPtr Handle, uint ProcessId, string ProcessName, string ProcessPath, string Title, bool Minimized);

internal interface IBrowserDesktop
{
    IntPtr Foreground { get; }
    IReadOnlyList<BrowserWindow> Windows();
    bool IsCurrent(BrowserWindow window);
    void RequestFocus(BrowserWindow window);
}

internal sealed class BrowserAutomation
{
    private readonly IBrowserDesktop desktop;
    private readonly Action<string> sendKeys;
    private readonly BrowserWindow? preferred;
    internal BrowserAutomation(BrowserWindow? preferred = null) : this(new NativeBrowserDesktop(), NativeInput.Hotkey, preferred) { }
    internal BrowserAutomation(IBrowserDesktop desktop, Action<string> sendKeys, BrowserWindow? preferred = null)
    { this.desktop = desktop; this.sendKeys = sendKeys; this.preferred = preferred; }

    internal static bool IsSupported(BrowserWindow window) =>
        window.ProcessName.ToLowerInvariant() is "chrome" or "msedge" or "firefox" or "brave" or "vivaldi" or "opera" or "chromium" or "octium";

    internal static BrowserWindow? Select(IReadOnlyList<BrowserWindow> windows, IntPtr foreground, BrowserWindow? preferred)
    {
        var browsers = windows.Where(IsSupported).ToArray();
        return browsers.FirstOrDefault(w => w.Handle == foreground)
            ?? browsers.FirstOrDefault(w => preferred != null && w.Handle == preferred.Handle && w.ProcessId == preferred.ProcessId
                && string.Equals(w.ProcessPath, preferred.ProcessPath, StringComparison.OrdinalIgnoreCase))
            ?? browsers.FirstOrDefault(w => !w.Minimized)
            ?? browsers.FirstOrDefault();
    }

    internal async Task<BrowserWindow> Focus(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Select(desktop.Windows(), desktop.Foreground, preferred)
            ?? throw new InvalidOperationException("No supported browser window is open. Open an Octo browser profile (Octium), Chrome, Edge, Firefox, Brave, Vivaldi, Opera, or Chromium first.");
        if (!desktop.IsCurrent(target)) throw new InvalidOperationException("The selected browser window closed. Run the workflow again.");
        if (desktop.Foreground != target.Handle) desktop.RequestFocus(target);
        var watch = Stopwatch.StartNew();
        long nextAttempt = 160;
        while (desktop.Foreground != target.Handle) {
            token.ThrowIfCancellationRequested();
            if (!desktop.IsCurrent(target)) throw new InvalidOperationException("The selected browser window closed while focusing it.");
            if (watch.ElapsedMilliseconds >= 2000) throw new InvalidOperationException("Windows did not allow browser focus. Click the intended browser window and run again. No shortcut was sent.");
            // SW_RESTORE is asynchronous. Retry activation after the window has had
            // time to restore, without changing Windows' foreground-lock settings.
            if (watch.ElapsedMilliseconds >= nextAttempt) { desktop.RequestFocus(target); nextAttempt += 160; }
            await Task.Delay(40, token);
        }
        token.ThrowIfCancellationRequested();
        EnsureForeground(target);
        return target;
    }

    internal async Task<BrowserWindow> OpenNewTab(CancellationToken token)
    {
        var target = await Focus(token);
        // Let restored windows establish their keyboard focus before injecting Ctrl+T.
        await Task.Delay(120, token);
        EnsureForeground(target);
        token.ThrowIfCancellationRequested();
        sendKeys("Ctrl+T");
        return target;
    }

    private void EnsureForeground(BrowserWindow target)
    {
        if (!desktop.IsCurrent(target) || desktop.Foreground != target.Handle)
            throw new InvalidOperationException("Browser focus changed. No shortcut was sent; run the workflow again.");
    }
}

internal sealed class BrowserTracker
{
    private IntPtr observed;
    internal BrowserWindow? LastBrowser { get; private set; }
    internal void Observe()
    {
        var current = NativeInput.GetForegroundWindow();
        if (current == observed) return;
        observed = current;
        var window = NativeBrowserDesktop.ReadWindow(current);
        if (window != null && BrowserAutomation.IsSupported(window)) LastBrowser = window;
    }
}

internal sealed class NativeBrowserDesktop : IBrowserDesktop
{
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    public IntPtr Foreground => NativeInput.GetForegroundWindow();
    public IReadOnlyList<BrowserWindow> Windows()
    {
        var windows = new List<BrowserWindow>();
        EnumWindows((hwnd, _) => { var window = ReadWindow(hwnd); if (window != null) windows.Add(window); return true; }, IntPtr.Zero);
        return windows;
    }
    internal static BrowserWindow? ReadWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return null;
        var className = new StringBuilder(256); GetClassName(hwnd, className, className.Capacity);
        if (className.ToString() is not ("Chrome_WidgetWin_1" or "MozillaWindowClass")) return null;
        var title = new StringBuilder(1024); GetWindowText(hwnd, title, title.Capacity);
        if (title.Length == 0) return null;
        GetWindowThreadProcessId(hwnd, out uint id);
        try {
            using var process = Process.GetProcessById((int)id);
            string? path = process.MainModule?.FileName;
            if (path == null) return null;
            var window = new BrowserWindow(hwnd, id, process.ProcessName, path, title.ToString(), IsIconic(hwnd));
            return BrowserAutomation.IsSupported(window) ? window : null;
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
            return null; // A window can disappear or be inaccessible while it is enumerated.
        }
    }
    public bool IsCurrent(BrowserWindow window)
    {
        if (!IsWindow(window.Handle) || !IsWindowVisible(window.Handle)) return false;
        GetWindowThreadProcessId(window.Handle, out uint id);
        return id == window.ProcessId;
    }
    public void RequestFocus(BrowserWindow window)
    {
        if (IsIconic(window.Handle)) ShowWindowAsync(window.Handle, 9); // SW_RESTORE
        SetForegroundWindow(window.Handle);
    }
}
