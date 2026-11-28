using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ScreenFlow;
public enum ActionKind {
    Click, DoubleClick, RightClick, MoveMouse, Scroll, TypeText, Hotkey, Delay, ClickImage, WaitForImage, CopyUrl, ReadClipboard, SetVariable, FocusBrowser, NewBrowserTab,
    // Flow control
    If, Else, EndIf, Loop, LoopWhile, EndLoop, ExitLoop, Label, GoTo, StopWorkflow,
    // Mouse
    Drag, MiddleClick,
    // Windows and system
    LaunchApp, OpenUrl, FocusWindow, WaitForWindow, CloseWindow, RunCommand, Screenshot,
    // Dialogs
    ShowMessage, AskInput,
    // Data and files
    SetClipboard, IncrementVariable, TransformText, GetDateTime, RandomNumber, ReadFile, WriteFile
}
public enum ConditionKind {
    Equals, NotEquals, Contains, NotContains, StartsWith, EndsWith, IsEmpty, IsNotEmpty, GreaterThan, LessThan, MatchesRegex,
    ImageVisible, ImageNotVisible, WindowExists, WindowNotExists, FileExists, FileNotExists
}
public enum TextOperation { Trim, Uppercase, Lowercase, Replace, RegexExtract, Length, Substring }
public sealed class Step : INotifyPropertyChanged
{
    private string _name = "New action";
    private bool _enabled = true;
    public string Name { get => _name; set { _name = value; Changed(); Changed(nameof(Description)); } }
    public bool Enabled { get => _enabled; set { _enabled = value; Changed(); } }
    public ActionKind Kind { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Amount { get; set; } = -3;
    public string Text { get; set; } = "";
    public string Keys { get; set; } = "Ctrl+Tab";
    public int DelayMs { get; set; } = 300;
    public int TimeoutMs { get; set; } = 10000;
    public double Confidence { get; set; } = 0.92;
    public bool MultiScale { get; set; }
    public string? ImageBase64 { get; set; }
    public string Variable { get; set; } = "url";
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public int Count { get; set; } = 3;
    public string Value { get; set; } = "";
    public string Target { get; set; } = "";
    public ConditionKind Condition { get; set; } = ConditionKind.Equals;
    public TextOperation Operation { get; set; } = TextOperation.Trim;
    public bool Wait { get; set; }
    private int _depth;
    [JsonIgnore] public int Depth { get => _depth; set { if (_depth == value) return; _depth = value; Changed(); Changed(nameof(Indent)); } }
    [JsonIgnore] public System.Windows.GridLength Indent => new(Math.Min(_depth, 12) * 22);
    [JsonIgnore] public string Icon => Catalog.Icon(Kind);
    [JsonIgnore] public string Description => Kind switch {
        ActionKind.Click or ActionKind.DoubleClick or ActionKind.RightClick or ActionKind.MiddleClick or ActionKind.MoveMouse => $"{Catalog.Label(Kind)} · ({X}, {Y})",
        ActionKind.Drag => $"Drag · ({X}, {Y}) → ({X2}, {Y2})",
        ActionKind.Scroll => $"Scroll · {Amount} wheel notches",
        ActionKind.TypeText => "Type · " + Short(Text),
        ActionKind.Hotkey => "Keyboard · " + Keys,
        ActionKind.Delay => $"Wait · {DelayMs:N0} ms",
        ActionKind.ClickImage or ActionKind.WaitForImage => $"Visual target · {Confidence:P0} confidence · {TimeoutMs / 1000.0:0.#}s timeout",
        ActionKind.CopyUrl => "Copy address bar → {{" + Variable + "}}",
        ActionKind.ReadClipboard => "Clipboard → {{" + Variable + "}}",
        ActionKind.FocusBrowser => "Find the active / last-used browser and focus it",
        ActionKind.NewBrowserTab => "Find browser → focus → Ctrl+T",
        ActionKind.If => "If " + Conditions.Describe(this),
        ActionKind.Else => "Otherwise",
        ActionKind.EndIf => "End of If block",
        ActionKind.Loop => $"Repeat {Count:N0} times" + (string.IsNullOrEmpty(Variable) ? "" : " · index → {{" + Variable + "}}"),
        ActionKind.LoopWhile => "While " + Conditions.Describe(this),
        ActionKind.EndLoop => "End of loop",
        ActionKind.ExitLoop => "Leave the innermost loop",
        ActionKind.Label => "Label · " + Text,
        ActionKind.GoTo => "Go to label · " + Text,
        ActionKind.StopWorkflow => "End the run here",
        ActionKind.LaunchApp => "Start · " + Short(Text) + (Wait ? " · wait for exit" : ""),
        ActionKind.OpenUrl => "Open · " + Short(Text),
        ActionKind.FocusWindow => "Focus window titled · " + Short(Text),
        ActionKind.WaitForWindow => $"Wait for window · {Short(Text)} · {TimeoutMs / 1000.0:0.#}s",
        ActionKind.CloseWindow => "Close window titled · " + Short(Text),
        ActionKind.RunCommand => (Wait ? "PowerShell · " : "cmd · ") + Short(Text) + (string.IsNullOrEmpty(Variable) ? "" : " → {{" + Variable + "}}"),
        ActionKind.Screenshot => "Save screen → " + Short(Text),
        ActionKind.ShowMessage => "Message · " + Short(Text),
        ActionKind.AskInput => "Ask · " + Short(Text) + " → {{" + Variable + "}}",
        ActionKind.SetClipboard => "Clipboard ← " + Short(Text),
        ActionKind.IncrementVariable => "{{" + Variable + "}} " + (Amount < 0 ? "− " + (-(long)Amount) : "+ " + Amount),
        ActionKind.TransformText => Operation + " · " + Short(Text) + " → {{" + Variable + "}}",
        ActionKind.GetDateTime => "Now (" + Text + ") → {{" + Variable + "}}",
        ActionKind.RandomNumber => $"Random {X}–{Y} → {{{{{Variable}}}}}",
        ActionKind.ReadFile => "Read " + Short(Text) + " → {{" + Variable + "}}",
        ActionKind.WriteFile => (Wait ? "Append to " : "Write to ") + Short(Text),
        _ => "Set {{" + Variable + "}}"
    };
    private static string Short(string text) { text = text.Replace("\r", "").Replace("\n", " "); return text.Length > 42 ? text[..42] + "…" : text; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() { Changed(nameof(Description)); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public Step Clone() => JsonSerializer.Deserialize<Step>(JsonSerializer.Serialize(this, Workflow.Json), Workflow.Json)!;
}
public sealed class Workflow
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled workflow";
    public int Repeat { get; set; } = 1;
    public int StartDelaySeconds { get; set; } = 3;
    public ObservableCollection<Step> Steps { get; set; } = new();
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static Workflow Load(string path)
    {
        if (new FileInfo(path).Length > 40 * 1024 * 1024) throw new InvalidDataException("Workflow exceeds the 40 MB limit.");
        var flow = JsonSerializer.Deserialize<Workflow>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty workflow.");
        if (flow.Version != 1 || flow.Steps == null || flow.Steps.Any(s => s == null)) throw new InvalidDataException("Unsupported or invalid workflow.");
        return flow;
    }
    public void Save(string path)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, true);
    }
    public static Workflow Example() => new() { Name = "Copy URL to another tab", Steps = new() {
        new Step { Kind = ActionKind.FocusBrowser, Name = "Find and focus the browser" },
        new Step { Kind = ActionKind.CopyUrl, Name = "Copy current page URL", Variable = "url" },
        new Step { Kind = ActionKind.Hotkey, Name = "Switch to the next tab", Keys = "Ctrl+Tab" },
        new Step { Kind = ActionKind.Delay, Name = "Let the tab settle", DelayMs = 600 },
        new Step { Kind = ActionKind.Click, Name = "Click your destination field", X = 600, Y = 450, Enabled = false },
        new Step { Kind = ActionKind.TypeText, Name = "Enter the copied URL", Text = "{{url}}", Enabled = false }
    }};
    public static Workflow NewTabExample() => new() { Name = "Open a new browser tab", Steps = new() {
        new Step { Kind = ActionKind.NewBrowserTab, Name = "Focus browser and open a new tab" }
    }};
}
public static class Catalog
{
    public static string Label(ActionKind kind) => kind switch {
        ActionKind.DoubleClick => "Double click", ActionKind.RightClick => "Right click", ActionKind.MoveMouse => "Move mouse",
        ActionKind.TypeText => "Type text", ActionKind.ClickImage => "Find image & click", ActionKind.WaitForImage => "Wait for image",
        ActionKind.CopyUrl => "Copy page URL", ActionKind.ReadClipboard => "Read clipboard", ActionKind.SetVariable => "Set variable",
        ActionKind.FocusBrowser => "Focus browser", ActionKind.NewBrowserTab => "New browser tab",
        ActionKind.If => "If", ActionKind.Else => "Else", ActionKind.EndIf => "End if", ActionKind.Loop => "Loop",
        ActionKind.LoopWhile => "Loop while", ActionKind.EndLoop => "End loop", ActionKind.ExitLoop => "Exit loop",
        ActionKind.GoTo => "Go to label", ActionKind.StopWorkflow => "Stop workflow", ActionKind.MiddleClick => "Middle click",
        ActionKind.Drag => "Drag and drop", ActionKind.LaunchApp => "Launch application", ActionKind.OpenUrl => "Open file or URL",
        ActionKind.FocusWindow => "Focus window", ActionKind.WaitForWindow => "Wait for window", ActionKind.CloseWindow => "Close window",
        ActionKind.RunCommand => "Run command", ActionKind.Screenshot => "Take screenshot", ActionKind.ShowMessage => "Show message",
        ActionKind.AskInput => "Ask for input", ActionKind.SetClipboard => "Set clipboard", ActionKind.IncrementVariable => "Increase variable",
        ActionKind.TransformText => "Transform text", ActionKind.GetDateTime => "Get date and time", ActionKind.RandomNumber => "Random number",
        ActionKind.ReadFile => "Read text file", ActionKind.WriteFile => "Write text file",
        _ => kind.ToString()
    };
    public static string Icon(ActionKind kind) => kind switch {
        ActionKind.Click or ActionKind.DoubleClick or ActionKind.RightClick => "\uE8B0",
        ActionKind.MoveMouse => "\uE7C2", ActionKind.Scroll => "\uE962",
        ActionKind.ClickImage or ActionKind.WaitForImage => "\uE8B9", ActionKind.Hotkey => "\uE765", ActionKind.TypeText => "\uE70F",
        ActionKind.Delay => "\uE823", ActionKind.CopyUrl => "\uE71B", ActionKind.ReadClipboard => "\uE77F",
        ActionKind.FocusBrowser => "\uE774", ActionKind.NewBrowserTab => "\uE710",
        ActionKind.If or ActionKind.Else or ActionKind.EndIf => "\uE8AB",
        ActionKind.Loop or ActionKind.LoopWhile or ActionKind.EndLoop or ActionKind.ExitLoop => "\uE895",
        ActionKind.Label => "\uE8EC", ActionKind.GoTo => "\uE7AD", ActionKind.StopWorkflow => "\uE71A",
        ActionKind.MiddleClick => "\uE8B0", ActionKind.Drag => "\uE759",
        ActionKind.LaunchApp => "\uE768", ActionKind.OpenUrl => "\uE8A7",
        ActionKind.FocusWindow or ActionKind.WaitForWindow or ActionKind.CloseWindow => "\uE737",
        ActionKind.RunCommand => "\uE756", ActionKind.Screenshot => "\uE722",
        ActionKind.ShowMessage => "\uE8BD", ActionKind.AskInput => "\uE8F2",
        ActionKind.SetClipboard => "\uE77F", ActionKind.IncrementVariable => "\uE948", ActionKind.TransformText => "\uE8D2",
        ActionKind.GetDateTime => "\uE787", ActionKind.RandomNumber => "\uE8B1",
        ActionKind.ReadFile or ActionKind.WriteFile => "\uE8A5",
        _ => "\uE943"
    };
}
public static class Variables
{
    public static bool ValidName(string? value) => value != null && Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");
    public static string Expand(string text, IReadOnlyDictionary<string, string> values) => Regex.Replace(text, @"\{\{([A-Za-z_][A-Za-z0-9_]*)\}\}", m =>
        values.TryGetValue(m.Groups[1].Value, out var value) ? value : throw new InvalidOperationException($"Variable '{m.Groups[1].Value}' is not set. Add a Set variable, Read clipboard, or Copy page URL action first."));
}
