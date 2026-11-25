using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ScreenFlow;
internal static class BrowserTests
{
    internal static void Run(List<string> results)
    {
        var chrome = new BrowserWindow(new IntPtr(101), 1001, "chrome", @"C:\Chrome\chrome.exe", "Chrome test", false);
        var edge = new BrowserWindow(new IntPtr(102), 1002, "msedge", @"C:\Edge\msedge.exe", "Edge test", false);
        // Synthetic window records only: these tests never enumerate or control Octo.
        var octo = new BrowserWindow(new IntPtr(103), 1003, "Octium", @"C:\Octo Browser\Octium.exe", "Profile A", false);
        var otherProfile = octo with { Handle = new IntPtr(104), ProcessId = 1004, Title = "Profile B" };
        void Check(string name, Action check) { check(); results.Add("PASS " + name); }
        void Assert(bool condition) { if (!condition) throw new Exception("Browser test assertion failed."); }
        Check("Browser selection prefers foreground, then remembered browser", () => {
            var windows = new[] { chrome, edge };
            Assert(BrowserAutomation.Select(windows, edge.Handle, chrome) == edge);
            Assert(BrowserAutomation.Select(windows, IntPtr.Zero, edge) == edge);
            Assert(BrowserAutomation.Select(windows, IntPtr.Zero, edge with { ProcessId = 999 }) == chrome);
        });
        Check("Browser selection supports Octium and excludes the profile manager", () => {
            Assert(BrowserAutomation.Select(new[] { octo }, octo.Handle, octo) == octo);
            Assert(BrowserAutomation.IsSupported(octo with { ProcessName = "chrome" }));
            Assert(!BrowserAutomation.IsSupported(octo with { ProcessName = "Octo Browser" }));
            Assert(!BrowserAutomation.IsSupported(chrome with { ProcessName = "ScreenFlow" }));
            Assert(BrowserAutomation.Select(new[] { chrome with { Minimized = true }, edge }, IntPtr.Zero, null) == edge);
        });
        Check("Multiple Octium profiles respect foreground and remembered identity", () => {
            var windows = new[] { chrome, octo, otherProfile };
            Assert(BrowserAutomation.Select(windows, otherProfile.Handle, octo) == otherProfile);
            Assert(BrowserAutomation.Select(windows, IntPtr.Zero, octo) == octo);
            Assert(BrowserAutomation.Select(windows, IntPtr.Zero, octo with { ProcessId = 999 }) == chrome);
        });
        Check("Synthetic Octium focus and Ctrl+T use the selected profile only", () => {
            var desktop = new FakeDesktop(chrome, octo with { Minimized = true }, otherProfile);
            var keys = new List<string>();
            var browser = new BrowserAutomation(desktop, chord => {
                Assert(desktop.Foreground == octo.Handle); keys.Add(chord);
            }, octo);
            var selected = Task.Run(() => browser.OpenNewTab(CancellationToken.None)).GetAwaiter().GetResult();
            Assert(selected.Handle == octo.Handle && desktop.FocusRequests == 1 && keys.Count == 1 && keys[0] == "Ctrl+T");
        });
        Check("New browser tab focuses before sending exactly Ctrl+T", () => {
            var desktop = new FakeDesktop(chrome);
            var keys = new List<string>();
            var browser = new BrowserAutomation(desktop, chord => { Assert(desktop.Foreground == chrome.Handle); keys.Add(chord); });
            Task.Run(() => browser.OpenNewTab(CancellationToken.None)).GetAwaiter().GetResult();
            Assert(desktop.FocusRequests == 1 && keys.Count == 1 && keys[0] == "Ctrl+T");
        });
        Check("Focus-only action sends no shortcut and new tab example round-trips", () => {
            var desktop = new FakeDesktop(chrome);
            int keys = 0;
            var runner = new WorkflowRunner(Dispatcher.CurrentDispatcher, new BrowserAutomation(desktop, _ => keys++));
            var flow = new Workflow { Steps = new() { new Step { Kind = ActionKind.FocusBrowser, DelayMs = 0 } } };
            Task.Run(() => runner.Run(flow, CancellationToken.None)).GetAwaiter().GetResult();
            var serialized = System.Text.Json.JsonSerializer.Serialize(Workflow.NewTabExample(), Workflow.Json);
            var restored = System.Text.Json.JsonSerializer.Deserialize<Workflow>(serialized, Workflow.Json)!;
            Assert(keys == 0 && desktop.FocusRequests == 1 && restored.Steps[0].Kind == ActionKind.NewBrowserTab);
        });
        Check("No browser or denied focus never sends Ctrl+T", () => {
            int keys = 0;
            foreach (var desktop in new[] { new FakeDesktop(), new FakeDesktop(chrome) { DenyFocus = true } }) {
                var browser = new BrowserAutomation(desktop, _ => keys++);
                try { Task.Run(() => browser.OpenNewTab(CancellationToken.None)).GetAwaiter().GetResult(); throw new Exception("Expected focus failure"); }
                catch (InvalidOperationException) { }
            }
            Assert(keys == 0);
        });
        Check("Lost browser focus prevents Ctrl+T", () => {
            var desktop = new FakeDesktop(chrome) { LoseFocusAfterReads = 4 };
            int keys = 0;
            try { Task.Run(() => new BrowserAutomation(desktop, _ => keys++).OpenNewTab(CancellationToken.None)).GetAwaiter().GetResult(); throw new Exception("Expected lost-focus failure"); }
            catch (InvalidOperationException) { }
            Assert(keys == 0);
        });
        Check("Cancellation during browser activation prevents shortcut input", () => {
            var desktop = new FakeDesktop(chrome) { DenyFocus = true };
            int keys = 0;
            using var stop = new CancellationTokenSource(60);
            try { Task.Run(() => new BrowserAutomation(desktop, _ => keys++).OpenNewTab(stop.Token)).GetAwaiter().GetResult(); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Assert(keys == 0);
        });
    }
    private sealed class FakeDesktop : IBrowserDesktop
    {
        private readonly BrowserWindow[] windows;
        private IntPtr foreground;
        private int reads;
        public int FocusRequests;
        public bool DenyFocus;
        public int LoseFocusAfterReads = int.MaxValue;
        public FakeDesktop(params BrowserWindow[] windows) { this.windows = windows; }
        public IntPtr Foreground => ++reads > LoseFocusAfterReads ? IntPtr.Zero : foreground;
        public IReadOnlyList<BrowserWindow> Windows() => windows;
        public bool IsCurrent(BrowserWindow window) => Array.Exists(windows, w => w.Handle == window.Handle && w.ProcessId == window.ProcessId);
        public void RequestFocus(BrowserWindow window) { FocusRequests++; if (!DenyFocus) foreground = window.Handle; }
    }
}
