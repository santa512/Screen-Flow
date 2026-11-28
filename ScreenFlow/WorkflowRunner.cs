using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ScreenFlow;
public sealed class WorkflowRunner
{
    private readonly Dispatcher dispatcher;
    private readonly BrowserAutomation browser;
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? Log;
    public event Action<int>? StepStarted;
    public WorkflowRunner(Dispatcher dispatcher) : this(dispatcher, new BrowserAutomation()) { }
    internal WorkflowRunner(Dispatcher dispatcher, BrowserAutomation browser) { this.dispatcher = dispatcher; this.browser = browser; }
    private static readonly ActionKind[] NeedsVariable = { ActionKind.CopyUrl, ActionKind.ReadClipboard, ActionKind.SetVariable, ActionKind.AskInput,
        ActionKind.IncrementVariable, ActionKind.TransformText, ActionKind.GetDateTime, ActionKind.RandomNumber, ActionKind.ReadFile };
    private static readonly ActionKind[] NeedsText = { ActionKind.LaunchApp, ActionKind.OpenUrl, ActionKind.FocusWindow, ActionKind.WaitForWindow,
        ActionKind.CloseWindow, ActionKind.RunCommand, ActionKind.Screenshot, ActionKind.ReadFile, ActionKind.WriteFile, ActionKind.Label, ActionKind.GoTo };
    public static bool IsFlowControl(ActionKind kind) => kind is ActionKind.If or ActionKind.Else or ActionKind.EndIf or ActionKind.Loop or ActionKind.LoopWhile
        or ActionKind.EndLoop or ActionKind.ExitLoop or ActionKind.Label or ActionKind.GoTo or ActionKind.StopWorkflow;
    public static bool HasCondition(ActionKind kind) => kind is ActionKind.If or ActionKind.LoopWhile;
    public static bool UsesImage(Step step) => step.Kind is ActionKind.ClickImage or ActionKind.WaitForImage || (HasCondition(step.Kind) && Conditions.UsesImage(step.Condition));
    public static bool UsesTimeout(Step step) => UsesImage(step) && !HasCondition(step.Kind)
        || step.Kind is ActionKind.FocusWindow or ActionKind.WaitForWindow or ActionKind.RunCommand;
    public static void Validate(Step step)
    {
        if (!Enum.IsDefined(step.Kind)) throw new ArgumentException("Unknown action type.");
        if (step.DelayMs < 0 || step.DelayMs > 3600000) throw new ArgumentException("Delay must be 0–3,600,000 ms.");
        if (UsesTimeout(step) && (step.TimeoutMs < 100 || step.TimeoutMs > 3600000)) throw new ArgumentException("Timeout must be 100–3,600,000 ms.");
        if (UsesImage(step)) {
            if (string.IsNullOrEmpty(step.ImageBase64)) throw new ArgumentException("Capture a visual target first.");
            if (step.Confidence < .5 || step.Confidence > 1 || !double.IsFinite(step.Confidence)) throw new ArgumentException("Confidence must be 0.50–1.00.");
            using var image = OpenCvSharp.Cv2.ImDecode(Convert.FromBase64String(step.ImageBase64), OpenCvSharp.ImreadModes.Color);
            if (image.Empty() || image.Width < 8 || image.Height < 8) throw new ArgumentException("Invalid visual target. Capture at least 8 × 8 pixels.");
        }
        if (HasCondition(step.Kind)) {
            if (!Enum.IsDefined(step.Condition)) throw new ArgumentException("Unknown condition.");
            if (!Conditions.UsesImage(step.Condition) && step.Condition is not (ConditionKind.IsEmpty or ConditionKind.IsNotEmpty) && string.IsNullOrWhiteSpace(step.Text))
                throw new ArgumentException("Enter the value to check, for example {{name}}.");
            if (step.Condition == ConditionKind.MatchesRegex) {
                try { _ = new System.Text.RegularExpressions.Regex(step.Value); }
                catch (ArgumentException) { throw new ArgumentException("The regular expression is not valid."); }
            }
        }
        if (Array.IndexOf(NeedsVariable, step.Kind) >= 0 && !Variables.ValidName(step.Variable))
            throw new ArgumentException("Variable names must start with a letter or underscore and contain only letters, digits, or underscores.");
        if (step.Kind is ActionKind.Loop or ActionKind.RunCommand && step.Variable.Length > 0 && !Variables.ValidName(step.Variable))
            throw new ArgumentException("Variable names must start with a letter or underscore and contain only letters, digits, or underscores. Leave it blank to skip.");
        if (Array.IndexOf(NeedsText, step.Kind) >= 0 && string.IsNullOrWhiteSpace(step.Text))
            throw new ArgumentException(step.Kind switch {
                ActionKind.Label or ActionKind.GoTo => "Enter a label name.",
                ActionKind.RunCommand => "Enter a command to run.",
                ActionKind.FocusWindow or ActionKind.WaitForWindow or ActionKind.CloseWindow => "Enter part of the window title.",
                _ => "Enter a file path, program or address."
            });
        if (step.Kind == ActionKind.Hotkey) {
            var keys = NativeInput.ParseKeys(step.Keys);
            if (keys.Contains((ushort)0x77)) throw new ArgumentException("F8 is reserved for emergency stop.");
        }
        if (step.Kind == ActionKind.Scroll && Math.Abs((long)step.Amount) > 1000) throw new ArgumentException("Use between -1000 and 1000 scroll notches.");
        if (step.Kind == ActionKind.Loop && (step.Count < 0 || step.Count > 1000000)) throw new ArgumentException("Loop count must be 0–1,000,000.");
        if (step.Kind == ActionKind.RandomNumber && step.X > step.Y) throw new ArgumentException("The minimum must not be larger than the maximum.");
        if (step.Kind == ActionKind.TransformText && !Enum.IsDefined(step.Operation)) throw new ArgumentException("Unknown text operation.");
        if (step.Kind == ActionKind.TransformText && step.Operation == TextOperation.RegexExtract) {
            try { _ = new System.Text.RegularExpressions.Regex(step.Value); }
            catch (ArgumentException) { throw new ArgumentException("The regular expression is not valid."); }
        }
        if (step.Kind == ActionKind.GetDateTime) {
            try { _ = DateTime.Now.ToString(step.Text, CultureInfo.CurrentCulture); }
            catch (FormatException) { throw new ArgumentException("The date format is not valid. Try yyyy-MM-dd HH:mm:ss."); }
        }
    }
    /// <summary>Checks every enabled action and the If / Loop / Label structure.</summary>
    public static FlowPlan ValidateWorkflow(Workflow workflow)
    {
        if (workflow.Repeat < 1 || workflow.Repeat > 10000) throw new ArgumentException("Repeat must be 1–10,000.");
        for (int i = 0; i < workflow.Steps.Count; i++) {
            var step = workflow.Steps[i];
            if (!step.Enabled) continue;
            try { Validate(step); }
            catch (ArgumentException ex) { throw new ArgumentException($"Step {i + 1} · {step.Name}: {ex.Message}", ex); }
        }
        return FlowPlan.Build(workflow.Steps);
    }
    public async Task Run(Workflow workflow, CancellationToken token)
    {
        var plan = ValidateWorkflow(workflow);
        var steps = workflow.Steps;
        Values.Clear();
        for (int round = 1; round <= workflow.Repeat; round++) {
            Log?.Invoke($"Cycle {round} / {workflow.Repeat}");
            var counters = new Dictionary<int, int>();
            bool continuing = false;
            int pc = 0;
            while (pc < steps.Count) {
                token.ThrowIfCancellationRequested();
                int i = pc;
                var step = steps[i];
                bool structural = step.Kind is ActionKind.Else or ActionKind.EndIf or ActionKind.EndLoop or ActionKind.Label;
                if (!step.Enabled && !structural) {
                    // A disabled If or Loop switches off its whole block.
                    pc = step.Kind is ActionKind.If or ActionKind.Loop or ActionKind.LoopWhile ? plan.End[i] + 1 : i + 1;
                    continue;
                }
                bool resumed = continuing; continuing = false;
                if (!structural) { StepStarted?.Invoke(i); if (!resumed) Log?.Invoke($"{i + 1:00}  {step.Name}"); }
                try {
                    switch (step.Kind) {
                        case ActionKind.If:
                            bool pass = Conditions.Evaluate(step, Values, token);
                            Log?.Invoke($"     Condition is {(pass ? "true" : "false")}");
                            pc = pass ? i + 1 : plan.ElseOf.TryGetValue(i, out int otherwise) ? otherwise + 1 : plan.End[i] + 1;
                            continue;
                        case ActionKind.Else: pc = plan.End[i] + 1; continue; // reached after the true branch finished
                        case ActionKind.EndIf: case ActionKind.Label: pc = i + 1; continue;
                        case ActionKind.Loop: case ActionKind.LoopWhile:
                            if (!resumed || !counters.ContainsKey(i)) counters[i] = 0; // Go to can enter a loop mid-body
                            bool again = step.Kind == ActionKind.Loop ? counters[i] < step.Count : Conditions.Evaluate(step, Values, token);
                            if (!again) { counters.Remove(i); Log?.Invoke("     Loop finished"); pc = plan.End[i] + 1; continue; }
                            counters[i]++;
                            if (step.Kind == ActionKind.Loop && step.Variable.Length > 0) Values[step.Variable] = counters[i].ToString(CultureInfo.InvariantCulture);
                            Log?.Invoke(step.Kind == ActionKind.Loop ? $"     Pass {counters[i]} / {step.Count}" : $"     Pass {counters[i]}");
                            pc = i + 1; continue;
                        case ActionKind.EndLoop: pc = plan.End[i]; continuing = true; continue;
                        case ActionKind.ExitLoop:
                            int loop = plan.LoopOf[i]; counters.Remove(loop);
                            pc = plan.End[loop] + 1; continue;
                        case ActionKind.GoTo: pc = plan.Labels[step.Text.Trim()]; continue;
                        case ActionKind.StopWorkflow: Log?.Invoke("     Stop workflow reached"); return;
                    }
                    await Execute(step, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { throw new InvalidOperationException($"Step {i + 1} · {step.Name}: {ex.Message}", ex); }
                if (step.Kind != ActionKind.Delay) await Task.Delay(step.DelayMs, token);
                pc = i + 1;
            }
        }
    }
    private async Task<string> ReadClipboard(CancellationToken token)
    {
        for (int i = 0; i < 20; i++) {
            token.ThrowIfCancellationRequested();
            try { return await dispatcher.InvokeAsync(() => Clipboard.ContainsText() ? Clipboard.GetText() : ""); }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(50, token); }
        }
        throw new InvalidOperationException("Clipboard is busy. Try again.");
    }
    private async Task WriteClipboard(string text, CancellationToken token)
    {
        for (int i = 0; i < 20; i++) {
            token.ThrowIfCancellationRequested();
            try { await dispatcher.InvokeAsync(() => { if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetDataObject(text, true); }); return; }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(50, token); }
        }
        throw new InvalidOperationException("Clipboard is busy. Try again.");
    }
    private string Expand(string text) => Variables.Expand(text, Values);
    private static string FullPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.Length == 0) throw new InvalidOperationException("Enter a file path.");
        return Path.GetFullPath(path);
    }
    private async Task<DesktopWindow> WaitForWindow(string title, int timeoutMs, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true) {
            token.ThrowIfCancellationRequested();
            var window = DesktopWindows.Find(title);
            if (window != null) return window;
            if (watch.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException($"No window with a title like '{title}' appeared within {timeoutMs / 1000.0:0.#}s.");
            await Task.Delay(200, token);
        }
    }
    private async Task RunCommand(Step step, CancellationToken token)
    {
        string command = Expand(step.Text);
        var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (step.Wait) {
            // -EncodedCommand avoids every quoting problem; force UTF-8 output so Unicode survives.
            string script = "[Console]::OutputEncoding=[Text.Encoding]::UTF8\n" + command;
            info.FileName = "powershell.exe";
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-ExecutionPolicy"); info.ArgumentList.Add("Bypass");
            info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        } else {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = "/d /s /c \"chcp 65001 >nul & " + command + "\"";
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("The command could not start.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(step.TimeoutMs);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException) {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException($"The command did not finish within {step.TimeoutMs / 1000.0:0.#}s and was stopped.");
        }
        string text = (await output).TrimEnd('\r', '\n'), error = (await errors).Trim();
        Values["exit_code"] = process.ExitCode.ToString(CultureInfo.InvariantCulture);
        if (step.Variable.Length > 0) Values[step.Variable] = text;
        Log?.Invoke($"     Exit code {process.ExitCode}" + (error.Length > 0 ? " · " + (error.Length > 300 ? error[..300] + "…" : error).Replace(Environment.NewLine, " ") : ""));
    }
    private async Task Execute(Step step, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (step.Kind) {
            case ActionKind.FocusBrowser:
                var focused = await browser.Focus(token);
                Log?.Invoke($"     Focused browser · {focused.ProcessName}");
                break;
            case ActionKind.NewBrowserTab:
                var tabBrowser = await browser.OpenNewTab(token);
                Log?.Invoke($"     Focused {tabBrowser.ProcessName} · sent Ctrl+T");
                break;
            case ActionKind.Click: case ActionKind.DoubleClick: case ActionKind.RightClick: case ActionKind.MiddleClick:
                await NativeInput.Click(step.X, step.Y, step.Kind, token); break;
            case ActionKind.MoveMouse: NativeInput.Move(step.X, step.Y); break;
            case ActionKind.Scroll: NativeInput.Scroll(step.Amount); break;
            case ActionKind.TypeText: await NativeInput.TypeText(Variables.Expand(step.Text, Values), token); break;
            case ActionKind.Hotkey: NativeInput.Hotkey(step.Keys); break;
            case ActionKind.Delay: await Task.Delay(step.DelayMs, token); break;
            case ActionKind.ReadClipboard: Values[step.Variable] = await ReadClipboard(token); break;
            case ActionKind.SetVariable: Values[step.Variable] = Variables.Expand(step.Text, Values); break;
            case ActionKind.CopyUrl:
                NativeInput.Hotkey("Ctrl+L");
                await Task.Delay(150, token);
                uint before = NativeInput.GetClipboardSequenceNumber();
                NativeInput.Hotkey("Ctrl+C");
                var clipboardWatch = Stopwatch.StartNew();
                while (NativeInput.GetClipboardSequenceNumber() == before) {
                    if (clipboardWatch.ElapsedMilliseconds > 2000) throw new InvalidOperationException("No URL was copied. Focus a browser page before running.");
                    await Task.Delay(50, token);
                }
                string url = await ReadClipboard(token);
                if (!Uri.TryCreate(url, UriKind.Absolute, out _)) throw new InvalidOperationException("The copied address is not a URL. Focus the browser first.");
                Values[step.Variable] = url;
                NativeInput.Hotkey("Escape");
                break;
            case ActionKind.Drag: await NativeInput.Drag(step.X, step.Y, step.X2, step.Y2, token); break;
            case ActionKind.LaunchApp: {
                string file = Expand(step.Text).Trim().Trim('"');
                var info = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(file)) { UseShellExecute = true, Arguments = Expand(step.Value) };
                string? folder = Path.GetDirectoryName(info.FileName);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) info.WorkingDirectory = folder;
                using var process = Process.Start(info);
                Log?.Invoke(process == null ? "     Started" : $"     Started process {process.Id}");
                if (step.Wait && process != null) { await process.WaitForExitAsync(token); Log?.Invoke($"     Exited with code {process.ExitCode}"); }
                break;
            }
            case ActionKind.OpenUrl:
                using (Process.Start(new ProcessStartInfo(Expand(step.Text).Trim()) { UseShellExecute = true })) { }
                break;
            case ActionKind.FocusWindow: {
                var window = await WaitForWindow(Expand(step.Text), step.TimeoutMs, token);
                DesktopWindows.Focus(window);
                var focusWatch = Stopwatch.StartNew();
                while (!DesktopWindows.IsForeground(window)) {
                    if (focusWatch.ElapsedMilliseconds > 1500) throw new InvalidOperationException($"Windows did not bring '{window.Title}' to the front. Click it once, then run again.");
                    await Task.Delay(50, token); DesktopWindows.Focus(window);
                }
                Log?.Invoke($"     Focused · {window.Title}");
                break;
            }
            case ActionKind.WaitForWindow: {
                string title = Expand(step.Text);
                if (!step.Wait) { Log?.Invoke($"     Found · {(await WaitForWindow(title, step.TimeoutMs, token)).Title}"); break; }
                var closeWatch = Stopwatch.StartNew();
                while (DesktopWindows.Find(title) != null) {
                    if (closeWatch.ElapsedMilliseconds >= step.TimeoutMs) throw new TimeoutException($"A window titled like '{title}' was still open after {step.TimeoutMs / 1000.0:0.#}s.");
                    await Task.Delay(200, token);
                }
                break;
            }
            case ActionKind.CloseWindow: {
                var window = DesktopWindows.Find(Expand(step.Text)) ?? throw new InvalidOperationException($"No open window has a title like '{step.Text}'.");
                DesktopWindows.Close(window);
                Log?.Invoke($"     Asked to close · {window.Title}");
                break;
            }
            case ActionKind.RunCommand: await RunCommand(step, token); break;
            case ActionKind.Screenshot: {
                string path = FullPath(Expand(step.Text));
                if (Path.GetExtension(path).Length == 0) path += ".png";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var shot = Vision.Capture();
                await File.WriteAllBytesAsync(path, Vision.Png(shot), token);
                Log?.Invoke($"     Saved {path}");
                break;
            }
            case ActionKind.ShowMessage: {
                string text = Expand(step.Text), title = Expand(step.Value);
                await dispatcher.InvokeAsync(() => RunDialogs.Message(title, text));
                break;
            }
            case ActionKind.AskInput: {
                string prompt = Expand(step.Text), fallback = Expand(step.Value);
                string? answer = await dispatcher.InvokeAsync(() => RunDialogs.Ask(prompt, fallback));
                if (answer == null) throw new OperationCanceledException("Input was cancelled.");
                Values[step.Variable] = answer;
                break;
            }
            case ActionKind.SetClipboard: await WriteClipboard(Expand(step.Text), token); break;
            case ActionKind.IncrementVariable: {
                string current = Values.TryGetValue(step.Variable, out var existing) && existing.Trim().Length > 0 ? existing : "0";
                double number = Conditions.Number(current) ?? throw new InvalidOperationException($"{{{{{step.Variable}}}}} is '{current}', which is not a number.");
                Values[step.Variable] = (number + step.Amount).ToString(CultureInfo.InvariantCulture);
                Log?.Invoke($"     {{{{{step.Variable}}}}} = {Values[step.Variable]}");
                break;
            }
            case ActionKind.TransformText:
                Values[step.Variable] = TextTools.Apply(step.Operation, Expand(step.Text), Expand(step.Value), Expand(step.Target)); break;
            case ActionKind.GetDateTime: Values[step.Variable] = DateTime.Now.ToString(step.Text, CultureInfo.CurrentCulture); break;
            case ActionKind.RandomNumber: Values[step.Variable] = Random.Shared.Next(step.X, step.Y + 1).ToString(CultureInfo.InvariantCulture); break;
            case ActionKind.ReadFile: {
                string path = FullPath(Expand(step.Text));
                if (new FileInfo(path).Length > 50 * 1024 * 1024) throw new InvalidOperationException("The file is larger than 50 MB.");
                Values[step.Variable] = await File.ReadAllTextAsync(path, token);
                break;
            }
            case ActionKind.WriteFile: {
                string path = FullPath(Expand(step.Text)), content = Expand(step.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (step.Wait) await File.AppendAllTextAsync(path, content, token); else await File.WriteAllTextAsync(path, content, token);
                break;
            }
            case ActionKind.ClickImage: case ActionKind.WaitForImage:
                var watch = Stopwatch.StartNew();
                double best = 0;
                while (true) {
                    token.ThrowIfCancellationRequested();
                    var found = Vision.FindOnScreen(step.ImageBase64!, step.MultiScale, token);
                    if (found != null) best = Math.Max(best, found.Score);
                    if (found != null && found.Score >= step.Confidence) {
                        Log?.Invoke($"     Target found · {found.Score:P1} at ({found.X}, {found.Y})");
                        if (step.Kind == ActionKind.ClickImage) await NativeInput.Click(found.X + found.Width / 2, found.Y + found.Height / 2, ActionKind.Click, token);
                        break;
                    }
                    if (watch.ElapsedMilliseconds >= step.TimeoutMs) throw new TimeoutException($"Visual target not found; best score {best:P1}, required {step.Confidence:P0}. Recapture at the current zoom or enable scale search.");
                    await Task.Delay(250, token);
                }
                break;
        }
    }
}
