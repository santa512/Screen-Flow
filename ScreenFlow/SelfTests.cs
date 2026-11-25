using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using OpenCvSharp;

namespace ScreenFlow;
public static class SelfTests
{
    public static void Run()
    {
        var results = new List<string>();
        void Check(string name, Action test) { test(); results.Add("PASS " + name); }
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected validation failure."); }
        Check("Native INPUT uses the correct x64 ABI", () => Assert(Marshal.SizeOf<NativeInput.Input>() == 40, "Incorrect INPUT size"));
        Check("Keyboard shortcuts and navigation keys", () => {
            Assert(NativeInput.ParseKeys("Ctrl+Shift+Tab").SequenceEqual(new ushort[] { 17, 16, 9 }), "Wrong shortcut");
            Assert(NativeInput.ParseKeys("Ctrl+1")[1] == 49, "Wrong digit key");
            Throws(() => NativeInput.ParseKeys("Ctrl+Banana"));
            Throws(() => WorkflowRunner.Validate(new Step { Kind = ActionKind.Hotkey, Keys = "F8" }));
        });
        Check("Variable expansion preserves Unicode and rejects missing values", () => {
            var values = new Dictionary<string, string> { ["url"] = "https://example.org/日本語" };
            Assert(Variables.Expand("Link: {{url}}", values) == "Link: https://example.org/日本語", "Expansion failed");
            try { Variables.Expand("{{missing}}", values); throw new Exception("Missing value was accepted"); } catch (InvalidOperationException) { }
            Assert(!Variables.ValidName("invalid name") && Variables.ValidName("item_1"), "Wrong name validation");
        });
        using var target = new Mat(32, 70, MatType.CV_8UC3);
        Cv2.Randu(target, new Scalar(0, 0, 0), new Scalar(255, 255, 255));
        Cv2.PutText(target, "GO", new Point(8, 23), HersheyFonts.HersheySimplex, .65, new Scalar(0, 240, 120), 2);
        using var desktop = new Mat(220, 420, MatType.CV_8UC3, new Scalar(235, 235, 235));
        using (var roi = new Mat(desktop, new Rect(151, 83, target.Width, target.Height))) target.CopyTo(roi);
        Check("OpenCV locates the exact target in a synthetic desktop", () => {
            var match = Vision.Find(desktop, target, false)!;
            Assert(match.Score > .999 && match.X == 151 && match.Y == 83, $"Bad detection: {match}");
        });
        Check("OpenCV scale search locates resized targets", () => {
            using var screen = new Mat(260, 420, MatType.CV_8UC3, new Scalar(220, 220, 220));
            using var resized = new Mat(); Cv2.Resize(target, resized, new Size(), 1.25, 1.25);
            using (var roi = new Mat(screen, new Rect(180, 110, resized.Width, resized.Height))) resized.CopyTo(roi);
            var match = Vision.Find(screen, target, true)!;
            Assert(match.Score > .99 && match.X == 180 && match.Y == 110, "Scale detection failed");
        });
        Check("Vision rejects plain targets and absent matches", () => {
            using var blank = new Mat(20, 20, MatType.CV_8UC3, new Scalar(255, 255, 255));
            Throws(() => Vision.Find(desktop, blank, false));
            using var absent = new Mat(240, 440, MatType.CV_8UC3); Cv2.Randu(absent, Scalar.All(0), Scalar.All(255));
            Assert(Vision.Find(absent, target, false)!.Score < .92, "Unexpected false positive");
        });
        Check("Workflow round trip embeds visual target and retains disabled steps", () => {
            var flow = Workflow.Example();
            flow.Steps.Add(new Step { Kind = ActionKind.ClickImage, ImageBase64 = Convert.ToBase64String(target.ImEncode(".png")), MultiScale = true });
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".screenflow.json");
            try {
                flow.Save(path); var restored = Workflow.Load(path);
                Assert(restored.Steps.Count == 7 && !restored.Steps[5].Enabled && restored.Steps[0].Kind == ActionKind.FocusBrowser, "Steps lost");
                Assert(restored.Steps[6].ImageBase64 == flow.Steps[6].ImageBase64 && restored.Steps[6].MultiScale, "Image lost");
            } finally { File.Delete(path); }
        });
        Check("Invalid timeouts, variables and confidence fail before execution", () => {
            Throws(() => WorkflowRunner.Validate(new Step { Kind = ActionKind.Delay, DelayMs = -1 }));
            Throws(() => WorkflowRunner.Validate(new Step { Kind = ActionKind.SetVariable, Variable = "bad name" }));
            Throws(() => WorkflowRunner.Validate(new Step { Kind = ActionKind.ClickImage }));
            Throws(() => WorkflowRunner.Validate(new Step { Kind = ActionKind.ClickImage, ImageBase64 = Convert.ToBase64String(target.ImEncode(".png")), Confidence = double.NaN }));
        });
        Check("Runner repeats, skips disabled actions and preserves variables", () => {
            var runner = new WorkflowRunner(Dispatcher.CurrentDispatcher);
            int count = 0; runner.StepStarted += _ => count++;
            var flow = new Workflow { Repeat = 2, Steps = new() {
                new Step { Kind = ActionKind.SetVariable, Variable = "a", Text = "Hello", DelayMs = 0 },
                new Step { Kind = ActionKind.SetVariable, Variable = "b", Text = "{{a}} 世界", DelayMs = 0 },
                new Step { Kind = ActionKind.Hotkey, Keys = "Invalid", Enabled = false }
            }};
            runner.Run(flow, CancellationToken.None).GetAwaiter().GetResult();
            Assert(count == 4 && runner.Values["b"] == "Hello 世界", "Runner sequence failed");
        });
        Check("Cancellation interrupts a long wait", () => {
            using var stop = new CancellationTokenSource(60);
            var runner = new WorkflowRunner(Dispatcher.CurrentDispatcher);
            try { Task.Run(() => runner.Run(new Workflow { Steps = new() { new Step { Kind = ActionKind.Delay, DelayMs = 10000 } } }, stop.Token)).GetAwaiter().GetResult(); throw new Exception("Cancellation failed"); }
            catch (OperationCanceledException) { }
        });
        BrowserTests.Run(results);
        File.WriteAllLines("self-test-results.txt", results.Append($"{results.Count} checks passed · OpenCV {Cv2.GetVersionString()}"));
    }
}
