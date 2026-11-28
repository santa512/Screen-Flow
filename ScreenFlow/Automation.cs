using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ScreenFlow;

public static class Conditions
{
    public static bool UsesSecondOperand(ConditionKind kind) => kind is ConditionKind.Equals or ConditionKind.NotEquals or ConditionKind.Contains
        or ConditionKind.NotContains or ConditionKind.StartsWith or ConditionKind.EndsWith or ConditionKind.GreaterThan or ConditionKind.LessThan or ConditionKind.MatchesRegex;
    public static bool UsesImage(ConditionKind kind) => kind is ConditionKind.ImageVisible or ConditionKind.ImageNotVisible;
    public static string Label(ConditionKind kind) => kind switch {
        ConditionKind.Equals => "equals", ConditionKind.NotEquals => "does not equal", ConditionKind.Contains => "contains",
        ConditionKind.NotContains => "does not contain", ConditionKind.StartsWith => "starts with", ConditionKind.EndsWith => "ends with",
        ConditionKind.IsEmpty => "is empty", ConditionKind.IsNotEmpty => "is not empty", ConditionKind.GreaterThan => "is greater than",
        ConditionKind.LessThan => "is less than", ConditionKind.MatchesRegex => "matches regular expression",
        ConditionKind.ImageVisible => "image is visible", ConditionKind.ImageNotVisible => "image is not visible",
        ConditionKind.WindowExists => "window exists", ConditionKind.WindowNotExists => "window does not exist",
        ConditionKind.FileExists => "file or folder exists", _ => "file or folder does not exist"
    };
    public static string Describe(Step step)
    {
        if (UsesImage(step.Condition)) return Label(step.Condition);
        if (step.Condition is ConditionKind.WindowExists or ConditionKind.WindowNotExists or ConditionKind.FileExists or ConditionKind.FileNotExists)
            return $"{Label(step.Condition)} · {step.Text}";
        return UsesSecondOperand(step.Condition) ? $"{step.Text} {Label(step.Condition)} {step.Value}" : $"{step.Text} {Label(step.Condition)}";
    }
    public static bool Evaluate(Step step, IReadOnlyDictionary<string, string> values, CancellationToken token)
    {
        switch (step.Condition) {
            case ConditionKind.ImageVisible: case ConditionKind.ImageNotVisible:
                var found = Vision.FindOnScreen(step.ImageBase64!, step.MultiScale, token);
                bool visible = found != null && found.Score >= step.Confidence;
                return step.Condition == ConditionKind.ImageVisible ? visible : !visible;
            case ConditionKind.WindowExists: return DesktopWindows.Find(Variables.Expand(step.Text, values)) != null;
            case ConditionKind.WindowNotExists: return DesktopWindows.Find(Variables.Expand(step.Text, values)) == null;
            case ConditionKind.FileExists: case ConditionKind.FileNotExists:
                string path = Variables.Expand(step.Text, values);
                bool exists = File.Exists(path) || Directory.Exists(path);
                return step.Condition == ConditionKind.FileExists ? exists : !exists;
        }
        return Compare(step.Condition, Variables.Expand(step.Text, values), Variables.Expand(step.Value, values));
    }
    public static bool Compare(ConditionKind kind, string left, string right) => kind switch {
        ConditionKind.Equals => Same(left, right),
        ConditionKind.NotEquals => !Same(left, right),
        ConditionKind.Contains => left.Contains(right, StringComparison.OrdinalIgnoreCase),
        ConditionKind.NotContains => !left.Contains(right, StringComparison.OrdinalIgnoreCase),
        ConditionKind.StartsWith => left.StartsWith(right, StringComparison.OrdinalIgnoreCase),
        ConditionKind.EndsWith => left.EndsWith(right, StringComparison.OrdinalIgnoreCase),
        ConditionKind.IsEmpty => string.IsNullOrWhiteSpace(left),
        ConditionKind.IsNotEmpty => !string.IsNullOrWhiteSpace(left),
        ConditionKind.GreaterThan => Order(left, right) > 0,
        ConditionKind.LessThan => Order(left, right) < 0,
        ConditionKind.MatchesRegex => Regex.IsMatch(left, right, RegexOptions.None, TimeSpan.FromSeconds(2)),
        _ => throw new InvalidOperationException("Unsupported comparison.")
    };
    // Numbers compare numerically ("10" > "9"); everything else compares as text, ignoring case.
    private static bool Same(string a, string b) => Number(a) is double x && Number(b) is double y ? x == y : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static int Order(string a, string b) => Number(a) is double x && Number(b) is double y ? x.CompareTo(y) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    internal static double? Number(string text) => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : null;
}

/// <summary>Resolves If / Else / End if, loops, labels and Go to into jump targets before a run.</summary>
public sealed class FlowPlan
{
    public Dictionary<int, int> End { get; } = new();        // If/Else/Loop/LoopWhile → matching End; EndLoop → its loop
    public Dictionary<int, int> ElseOf { get; } = new();     // If → Else
    public Dictionary<int, int> LoopOf { get; } = new();     // ExitLoop → innermost loop
    public Dictionary<string, int> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public static bool IsLoop(ActionKind kind) => kind is ActionKind.Loop or ActionKind.LoopWhile;
    public static FlowPlan Build(IList<Step> steps)
    {
        var plan = new FlowPlan();
        var open = new Stack<int>();
        string At(int i) => $"Step {i + 1} · {steps[i].Name}";
        for (int i = 0; i < steps.Count; i++) {
            var kind = steps[i].Kind;
            switch (kind) {
                case ActionKind.If: case ActionKind.Loop: case ActionKind.LoopWhile: open.Push(i); break;
                case ActionKind.Else:
                    if (open.Count == 0 || steps[open.Peek()].Kind != ActionKind.If) throw new ArgumentException($"{At(i)}: Else must be inside an If block.");
                    if (plan.ElseOf.ContainsKey(open.Peek())) throw new ArgumentException($"{At(i)}: an If block can only have one Else.");
                    plan.ElseOf[open.Peek()] = i; break;
                case ActionKind.EndIf:
                    if (open.Count == 0 || steps[open.Peek()].Kind != ActionKind.If) throw new ArgumentException($"{At(i)}: End if has no matching If.");
                    int start = open.Pop(); plan.End[start] = i;
                    if (plan.ElseOf.TryGetValue(start, out int otherwise)) plan.End[otherwise] = i;
                    break;
                case ActionKind.EndLoop:
                    if (open.Count == 0 || !IsLoop(steps[open.Peek()].Kind)) throw new ArgumentException($"{At(i)}: End loop has no matching Loop.");
                    int loop = open.Pop(); plan.End[loop] = i; plan.End[i] = loop; break;
                case ActionKind.ExitLoop:
                    int? inner = open.Cast<int?>().FirstOrDefault(o => IsLoop(steps[o!.Value].Kind));
                    if (inner == null) throw new ArgumentException($"{At(i)}: Exit loop must be inside a Loop.");
                    plan.LoopOf[i] = inner.Value; break;
                case ActionKind.Label:
                    string name = steps[i].Text.Trim();
                    if (name.Length == 0) throw new ArgumentException($"{At(i)}: give the label a name.");
                    if (!plan.Labels.TryAdd(name, i)) throw new ArgumentException($"{At(i)}: label '{name}' is used more than once.");
                    break;
            }
        }
        if (open.Count > 0) {
            int i = open.Peek();
            throw new ArgumentException($"{At(i)}: add a matching {(steps[i].Kind == ActionKind.If ? "End if" : "End loop")}.");
        }
        for (int i = 0; i < steps.Count; i++)
            if (steps[i].Kind == ActionKind.GoTo && !plan.Labels.ContainsKey(steps[i].Text.Trim()))
                throw new ArgumentException($"{At(i)}: no label named '{steps[i].Text.Trim()}'.");
        return plan;
    }
    /// <summary>Nesting depth for list indentation; tolerant of unfinished blocks while editing.</summary>
    public static void ApplyDepth(IList<Step> steps)
    {
        int depth = 0;
        foreach (var step in steps) {
            if (step.Kind is ActionKind.EndIf or ActionKind.EndLoop or ActionKind.Else) depth = Math.Max(0, depth - 1);
            step.Depth = depth;
            if (step.Kind is ActionKind.If or ActionKind.Loop or ActionKind.LoopWhile or ActionKind.Else) depth++;
        }
    }
}

public static class TextTools
{
    public static string Apply(TextOperation operation, string input, string find, string replacement) => operation switch {
        TextOperation.Trim => input.Trim(),
        TextOperation.Uppercase => input.ToUpperInvariant(),
        TextOperation.Lowercase => input.ToLowerInvariant(),
        TextOperation.Replace => find.Length == 0 ? throw new InvalidOperationException("Enter the text to find.") : input.Replace(find, replacement, StringComparison.Ordinal),
        TextOperation.RegexExtract => Extract(input, find),
        TextOperation.Length => input.Length.ToString(CultureInfo.InvariantCulture),
        TextOperation.Substring => Substring(input, find, replacement),
        _ => throw new InvalidOperationException("Unsupported text operation.")
    };
    private static string Extract(string input, string pattern)
    {
        var match = Regex.Match(input, pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        if (!match.Success) return "";
        return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
    }
    private static string Substring(string input, string start, string length)
    {
        if (!int.TryParse(start, out int from) || from < 0) throw new InvalidOperationException("Start position must be a whole number, 0 or more.");
        if (from >= input.Length) return "";
        if (string.IsNullOrWhiteSpace(length)) return input[from..];
        if (!int.TryParse(length, out int count) || count < 0) throw new InvalidOperationException("Length must be a whole number, 0 or more.");
        return input.Substring(from, Math.Min(count, input.Length - from));
    }
}

internal sealed record DesktopWindow(IntPtr Handle, string Title, uint ProcessId);
internal static class DesktopWindows
{
    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    /// <summary>Visible, titled, unowned top-level windows in Z order (frontmost first).</summary>
    internal static IReadOnlyList<DesktopWindow> All()
    {
        var windows = new List<DesktopWindow>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) => {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero) return true; // GW_OWNER
            var title = new StringBuilder(1024);
            if (GetWindowText(hwnd, title, title.Capacity) == 0) return true;
            GetWindowThreadProcessId(hwnd, out uint id);
            if (id != self) windows.Add(new DesktopWindow(hwnd, title.ToString(), id));
            return true;
        }, IntPtr.Zero);
        return windows;
    }
    /// <summary>Case-insensitive title match; * wildcards are allowed, otherwise the title only has to contain the text.</summary>
    internal static DesktopWindow? Find(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidOperationException("Enter part of the window title.");
        string pattern = title.Contains('*') ? "^" + Regex.Escape(title.Trim()).Replace("\\*", ".*") + "$" : Regex.Escape(title.Trim());
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return All().FirstOrDefault(w => regex.IsMatch(w.Title));
    }
    internal static void Focus(DesktopWindow window)
    {
        if (IsIconic(window.Handle)) ShowWindowAsync(window.Handle, 9); // SW_RESTORE
        SetForegroundWindow(window.Handle);
    }
    internal static bool IsForeground(DesktopWindow window) => NativeInput.GetForegroundWindow() == window.Handle;
    internal static void Close(DesktopWindow window) => PostMessage(window.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
}

/// <summary>Small modal dialogs shown on top of everything while a workflow runs.</summary>
internal static class RunDialogs
{
    internal static void Message(string title, string text) =>
        MessageBox.Show(text, string.IsNullOrWhiteSpace(title) ? "ScreenFlow" : title, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
    /// <summary>Returns null when the person cancels.</summary>
    internal static string? Ask(string prompt, string defaultValue)
    {
        var window = new Window { Title = "ScreenFlow", Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = true };
        window.SetResourceReference(Window.IconProperty, "AppLogo");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(prompt) ? "Enter a value" : prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var box = new TextBox { Text = defaultValue, MinHeight = 35 };
        System.Windows.Automation.AutomationProperties.SetName(box, prompt);
        panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) => { window.Activate(); box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
