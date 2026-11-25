using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ScreenFlow;
// Exercises the product's real screenshot and SendInput pipeline against our own WPF fixture.
// This harness never opens or activates a browser or another application's window.
internal static class IntegrationTests
{
    public static async Task Run(Window editor, bool browserOnly = false)
    {
        var results = new List<string>();
        string? oldClipboard = null;
        bool restoreClipboard = false;
        if (Clipboard.ContainsText()) { oldClipboard = Clipboard.GetText(); restoreClipboard = true; }
        var practice = new PracticeWindow { Topmost = true, Title = "ScreenFlow Practice — ready for check" };
        var start = new TaskCompletionSource<bool>();
        var startButton = new Button { Content = WindowsUi.Label("\uE768", "Start local check"), Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(startButton, "Start local check");
        startButton.Click += (_, _) => { startButton.IsEnabled = false; start.TrySetResult(true); };
        ((StackPanel)practice.Content).Children.Insert(0, startButton);
        practice.Show(); practice.Activate();
        try {
            // A real click grants this process foreground-input rights, matching a
            // user pressing Run. Merely activating it from a helper does not do so.
            await start.Task.WaitAsync(TimeSpan.FromSeconds(60));
            // Starting a process in the background does not guarantee foreground input rights.
            // Never inject anything unless the intended fixture owns the foreground window.
            var handle = new System.Windows.Interop.WindowInteropHelper(practice).Handle;
            var focusWait = Stopwatch.StartNew();
            while (NativeInput.GetForegroundWindow() != handle) {
                if (focusWait.ElapsedMilliseconds > 30000) throw new Exception("Activate the ScreenFlow Practice test window to run the desktop test.");
                await Task.Delay(100);
            }
            practice.Title = "ScreenFlow Practice — automated check running";
            await Task.Delay(600);
            // Inject only the practice window as a browser candidate. Real browser discovery
            // is never used in this harness, including on machines running Octo / Octium.
            var fixture = new BrowserWindow(handle, (uint)Environment.ProcessId, "chrome", @"C:\ScreenFlow-test-fixture.exe", practice.Title, true);
            practice.WindowState = WindowState.Minimized;
            editor.Show();
            editor.WindowState = WindowState.Normal;
            editor.Activate();
            await Task.Delay(150);
            var browserRunner = new WorkflowRunner(editor.Dispatcher, new BrowserAutomation(new FixtureBrowserDesktop(fixture), NativeInput.Hotkey));
            await Task.Run(() => browserRunner.Run(Workflow.NewTabExample(), CancellationToken.None));
            if (NativeInput.GetForegroundWindow() != handle || practice.NewTabsOpened != 1 || practice.WindowState == WindowState.Minimized)
                throw new Exception("Browser focus / restore / Ctrl+T integration check failed.");
            results.Add("PASS Restore and focus the isolated fixture → real Ctrl+T key input");
            if (browserOnly) { File.WriteAllLines("browser-test-results.txt", results); return; }
            practice.PracticeInput.Focus();
            await Task.Delay(200);
            string fieldImage = CaptureElement(practice.PracticeInput, 4);
            string buttonImage = CaptureElement(practice.CompleteButton);
            var runner = new WorkflowRunner(editor.Dispatcher, new BrowserAutomation(new FixtureBrowserDesktop(fixture), NativeInput.Hotkey));
            runner.StepStarted += _ => { if (NativeInput.GetForegroundWindow() != handle) throw new InvalidOperationException("Practice window lost foreground focus. No further test input will be sent."); };
            runner.Log += text => results.Add(text);
            var flow = new Workflow { Steps = new() {
                new Step { Kind = ActionKind.CopyUrl, Name = "Copy address from practice", Variable = "url" },
                new Step { Kind = ActionKind.ClickImage, Name = "Find text field", ImageBase64 = fieldImage, Confidence = .95 },
                new Step { Kind = ActionKind.Hotkey, Name = "Select field contents", Keys = "Ctrl+A" },
                new Step { Kind = ActionKind.TypeText, Name = "Type Unicode and URL", Text = "Hello 世界 · {{url}}" },
                new Step { Kind = ActionKind.ClickImage, Name = "Find and click practice button", ImageBase64 = buttonImage, Confidence = .95 }
            }};
            await Task.Run(() => runner.Run(flow, CancellationToken.None));
            string expected = "Hello 世界 · https://example.invalid/practice";
            if (practice.PracticeInput.Text != expected) throw new Exception("Native text input did not reach the fixture: " + practice.PracticeInput.Text);
            if (practice.ResultLabel.Text != "Success! Received: " + expected) throw new Exception("Visual button click failed: " + practice.ResultLabel.Text);
            results.Add("PASS Real screenshot → OpenCV field match → native mouse click");
            results.Add("PASS Native Ctrl+L / Ctrl+C → clipboard URL variable");
            results.Add("PASS Native Ctrl+A → Unicode keyboard input → variable substitution");
            results.Add("PASS Real screenshot → OpenCV button match → native click → expected result");
            var position = practice.PracticeInput.PointToScreen(new Point(20, 20));
            var coordinateFlow = new Workflow { Steps = new() {
                new Step { Kind = ActionKind.Click, X = (int)position.X, Y = (int)position.Y },
                new Step { Kind = ActionKind.Hotkey, Keys = "Ctrl+A" },
                new Step { Kind = ActionKind.TypeText, Text = "Coordinate click passed" }
            }};
            await Task.Run(() => runner.Run(coordinateFlow, CancellationToken.None));
            if (practice.PracticeInput.Text != "Coordinate click passed") throw new Exception("Coordinate input failed");
            results.Add("PASS Native coordinate click → keyboard shortcut → text input");
            File.WriteAllLines("ui-test-results.txt", results);
        } finally {
            practice.Close();
            if (restoreClipboard) Clipboard.SetText(oldClipboard!);
        }
    }
    private sealed class FixtureBrowserDesktop : IBrowserDesktop
    {
        private readonly BrowserWindow fixture;
        private readonly NativeBrowserDesktop native = new();
        internal FixtureBrowserDesktop(BrowserWindow fixture) { this.fixture = fixture; }
        public IntPtr Foreground => native.Foreground;
        public IReadOnlyList<BrowserWindow> Windows() => new[] { fixture };
        public bool IsCurrent(BrowserWindow window) => window.Handle == fixture.Handle && native.IsCurrent(fixture);
        public void RequestFocus(BrowserWindow window) { if (window.Handle != fixture.Handle) throw new InvalidOperationException("Non-fixture window"); native.RequestFocus(fixture); }
    }
    private static string CaptureElement(FrameworkElement element, int inset = 0)
    {
        var location = element.PointToScreen(new Point(inset, inset));
        var end = element.PointToScreen(new Point(element.ActualWidth - inset, element.ActualHeight - inset));
        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var desktop = Vision.Capture();
        using var crop = desktop.Clone(new System.Drawing.Rectangle((int)location.X - virtualScreen.Left, (int)location.Y - virtualScreen.Top,
            (int)(end.X - location.X), (int)(end.Y - location.Y)), PixelFormat.Format24bppRgb);
        return Convert.ToBase64String(Vision.Png(crop));
    }
}
