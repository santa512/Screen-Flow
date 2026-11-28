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
        Check("If / Else / Loop / Exit loop / Go to run in the right order", () => {
            Step S(ActionKind kind, string text = "", string variable = "x", ConditionKind condition = ConditionKind.Equals, string value = "", int count = 3, bool enabled = true) =>
                new() { Kind = kind, Text = text, Variable = variable, Condition = condition, Value = value, Count = count, Amount = 1, DelayMs = 0, Enabled = enabled };
            string Trace(params Step[] steps) {
                var runner = new WorkflowRunner(Dispatcher.CurrentDispatcher);
                var all = new List<Step> { S(ActionKind.SetVariable, "", "trace") };
                foreach (var step in steps) {
                    all.Add(step);
                    // Record every visible action by appending its text to {{trace}}.
                    if (step.Kind == ActionKind.SetClipboard) { step.Kind = ActionKind.SetVariable; step.Variable = "trace"; step.Text = "{{trace}}" + step.Text + ","; }
                }
                runner.Run(new Workflow { Steps = new(all) }, CancellationToken.None).GetAwaiter().GetResult();
                return runner.Values["trace"].TrimEnd(',');
            }
            var T = ActionKind.SetClipboard;
            Assert(Trace(S(ActionKind.SetVariable, "5", "n"), S(ActionKind.If, "{{n}}", condition: ConditionKind.GreaterThan, value: "10"), S(T, "big"), S(ActionKind.Else), S(T, "small"), S(ActionKind.EndIf), S(T, "end")) == "small,end", "If/Else");
            Assert(Trace(S(ActionKind.Loop, variable: "i", count: 2), S(ActionKind.Loop, variable: "j", count: 2), S(T, "{{i}}{{j}}"), S(ActionKind.EndLoop), S(ActionKind.EndLoop)) == "11,12,21,22", "Nested loops");
            Assert(Trace(S(ActionKind.Loop, variable: "i", count: 10), S(ActionKind.If, "{{i}}", value: "3"), S(ActionKind.ExitLoop), S(ActionKind.EndIf), S(T, "{{i}}"), S(ActionKind.EndLoop), S(T, "out")) == "1,2,out", "Exit loop");
            Assert(Trace(S(ActionKind.SetVariable, "0", "c"), S(ActionKind.LoopWhile, "{{c}}", condition: ConditionKind.LessThan, value: "3"), S(ActionKind.IncrementVariable, variable: "c"), S(T, "{{c}}"), S(ActionKind.EndLoop)) == "1,2,3", "Loop while");
            Assert(Trace(S(ActionKind.SetVariable, "0", "c"), S(ActionKind.Label, "top"), S(ActionKind.IncrementVariable, variable: "c"), S(ActionKind.If, "{{c}}", condition: ConditionKind.LessThan, value: "3"), S(ActionKind.GoTo, "top"), S(ActionKind.EndIf), S(T, "{{c}}"), S(ActionKind.StopWorkflow), S(T, "never")) == "3", "Go to / Stop");
            Assert(Trace(S(ActionKind.Loop, count: 5, enabled: false), S(T, "x"), S(ActionKind.EndLoop), S(T, "end")) == "end", "Disabled loop");
        });
        Check("Block structure errors are reported before running", () => {
            Throws(() => FlowPlan.Build(new List<Step> { new() { Kind = ActionKind.If } }));
            Throws(() => FlowPlan.Build(new List<Step> { new() { Kind = ActionKind.Loop }, new() { Kind = ActionKind.EndIf } }));
            Throws(() => FlowPlan.Build(new List<Step> { new() { Kind = ActionKind.ExitLoop } }));
            Throws(() => FlowPlan.Build(new List<Step> { new() { Kind = ActionKind.GoTo, Text = "missing" } }));
            Throws(() => WorkflowRunner.Validate(new Step { Kind = ActionKind.GetDateTime, Variable = "now", Text = "%" }));
            Throws(() => WorkflowRunner.Validate(new Step { Kind = ActionKind.RandomNumber, Variable = "r", X = 5, Y = 1 }));
        });
        Check("Text, date, random, file and command actions", () => {
            var runner = new WorkflowRunner(Dispatcher.CurrentDispatcher);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
            try {
                runner.Run(new Workflow { Steps = new() {
                    new Step { Kind = ActionKind.TransformText, Operation = TextOperation.RegexExtract, Text = "Order #4521 shipped", Value = @"#(\d+)", Variable = "id", DelayMs = 0 },
                    new Step { Kind = ActionKind.GetDateTime, Text = "yyyy", Variable = "year", DelayMs = 0 },
                    new Step { Kind = ActionKind.RandomNumber, X = 7, Y = 7, Variable = "r", DelayMs = 0 },
                    new Step { Kind = ActionKind.WriteFile, Text = path, Value = "{{id}}-日本", DelayMs = 0 },
                    new Step { Kind = ActionKind.WriteFile, Text = path, Value = "+", Wait = true, DelayMs = 0 },
                    new Step { Kind = ActionKind.ReadFile, Text = path, Variable = "back", DelayMs = 0 },
                    new Step { Kind = ActionKind.RunCommand, Text = "echo hello", Variable = "out", TimeoutMs = 20000, DelayMs = 0 }
                }}, CancellationToken.None).GetAwaiter().GetResult();
                Assert(runner.Values["id"] == "4521" && runner.Values["year"] == DateTime.Now.Year.ToString() && runner.Values["r"] == "7", "Data actions failed");
                Assert(runner.Values["back"] == "4521-日本+", "File round trip failed");
                Assert(runner.Values["out"] == "hello" && runner.Values["exit_code"] == "0", $"Command failed: '{runner.Values["out"]}'");
            } finally { File.Delete(path); }
        });
        BrowserTests.Run(results);
        File.WriteAllLines("self-test-results.txt", results.Append($"{results.Count} checks passed · OpenCV {Cv2.GetVersionString()}"));
    }
}
