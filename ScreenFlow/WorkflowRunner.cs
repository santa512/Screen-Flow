using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public static void Validate(Step step)
    {
        if (!Enum.IsDefined(step.Kind)) throw new ArgumentException("Unknown action type.");
        if (step.DelayMs < 0 || step.DelayMs > 3600000) throw new ArgumentException("Delay must be 0–3,600,000 ms.");
        if (step.Kind is ActionKind.ClickImage or ActionKind.WaitForImage) {
            if (string.IsNullOrEmpty(step.ImageBase64)) throw new ArgumentException("Capture a visual target first.");
            if (step.Confidence < .5 || step.Confidence > 1 || !double.IsFinite(step.Confidence)) throw new ArgumentException("Confidence must be 0.50–1.00.");
            if (step.TimeoutMs < 100 || step.TimeoutMs > 3600000) throw new ArgumentException("Image timeout must be 100–3,600,000 ms.");
            using var image = OpenCvSharp.Cv2.ImDecode(Convert.FromBase64String(step.ImageBase64), OpenCvSharp.ImreadModes.Color);
            if (image.Empty() || image.Width < 8 || image.Height < 8) throw new ArgumentException("Invalid visual target. Capture at least 8 × 8 pixels.");
        }
        if (step.Kind is ActionKind.CopyUrl or ActionKind.ReadClipboard or ActionKind.SetVariable && !Variables.ValidName(step.Variable))
            throw new ArgumentException("Variable names must start with a letter or underscore and contain only letters, digits, or underscores.");
        if (step.Kind == ActionKind.Hotkey) {
            var keys = NativeInput.ParseKeys(step.Keys);
            if (keys.Contains((ushort)0x77)) throw new ArgumentException("F8 is reserved for emergency stop.");
        }
        if (step.Kind == ActionKind.Scroll && Math.Abs((long)step.Amount) > 1000) throw new ArgumentException("Use between -1000 and 1000 scroll notches.");
    }
    public async Task Run(Workflow workflow, CancellationToken token)
    {
        if (workflow.Repeat < 1 || workflow.Repeat > 10000) throw new ArgumentException("Repeat must be 1–10,000.");
        foreach (var step in workflow.Steps.Where(s => s.Enabled)) Validate(step);
        Values.Clear();
        for (int round = 1; round <= workflow.Repeat; round++) {
            Log?.Invoke($"Cycle {round} / {workflow.Repeat}");
            for (int i = 0; i < workflow.Steps.Count; i++) {
                token.ThrowIfCancellationRequested();
                var step = workflow.Steps[i];
                if (!step.Enabled) continue;
                StepStarted?.Invoke(i);
                Log?.Invoke($"{i + 1:00}  {step.Name}");
                try { await Execute(step, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { throw new InvalidOperationException($"Step {i + 1} · {step.Name}: {ex.Message}", ex); }
                if (step.Kind != ActionKind.Delay) await Task.Delay(step.DelayMs, token);
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
            case ActionKind.Click: case ActionKind.DoubleClick: case ActionKind.RightClick:
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
