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
public enum ActionKind { Click, DoubleClick, RightClick, MoveMouse, Scroll, TypeText, Hotkey, Delay, ClickImage, WaitForImage, CopyUrl, ReadClipboard, SetVariable, FocusBrowser, NewBrowserTab }
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
    [JsonIgnore] public string Icon => Catalog.Icon(Kind);
    [JsonIgnore] public string Description => Kind switch {
        ActionKind.Click or ActionKind.DoubleClick or ActionKind.RightClick or ActionKind.MoveMouse => $"{Catalog.Label(Kind)} · ({X}, {Y})",
        ActionKind.Scroll => $"Scroll · {Amount} wheel notches",
        ActionKind.TypeText => "Type · " + (Text.Length > 42 ? Text[..42] + "…" : Text.Replace("\n", " ")),
        ActionKind.Hotkey => "Keyboard · " + Keys,
        ActionKind.Delay => $"Wait · {DelayMs:N0} ms",
        ActionKind.ClickImage or ActionKind.WaitForImage => $"Visual target · {Confidence:P0} confidence · {TimeoutMs / 1000.0:0.#}s timeout",
        ActionKind.CopyUrl => "Copy address bar → {{" + Variable + "}}",
        ActionKind.ReadClipboard => "Clipboard → {{" + Variable + "}}",
        ActionKind.FocusBrowser => "Find the active / last-used browser and focus it",
        ActionKind.NewBrowserTab => "Find browser → focus → Ctrl+T",
        _ => "Set {{" + Variable + "}}"
    };
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
        ActionKind.FocusBrowser => "Focus browser", ActionKind.NewBrowserTab => "New browser tab", _ => kind.ToString()
    };
    public static string Icon(ActionKind kind) => kind switch {
        ActionKind.Click or ActionKind.DoubleClick or ActionKind.RightClick => "\uE8B0",
        ActionKind.MoveMouse => "\uE7C2", ActionKind.Scroll => "\uE962",
        ActionKind.ClickImage or ActionKind.WaitForImage => "\uE8B9", ActionKind.Hotkey => "\uE765", ActionKind.TypeText => "\uE70F",
        ActionKind.Delay => "\uE823", ActionKind.CopyUrl => "\uE71B", ActionKind.ReadClipboard => "\uE77F",
        ActionKind.FocusBrowser => "\uE774", ActionKind.NewBrowserTab => "\uE710", _ => "\uE943"
    };
}
public static class Variables
{
    public static bool ValidName(string? value) => value != null && Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");
    public static string Expand(string text, IReadOnlyDictionary<string, string> values) => Regex.Replace(text, @"\{\{([A-Za-z_][A-Za-z0-9_]*)\}\}", m =>
        values.TryGetValue(m.Groups[1].Value, out var value) ? value : throw new InvalidOperationException($"Variable '{m.Groups[1].Value}' is not set. Add a Set variable, Read clipboard, or Copy page URL action first."));
}
