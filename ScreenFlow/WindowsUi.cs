using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ScreenFlow;
// Content only: Windows' built-in Fluent theme owns every control template.
public sealed class IconLabel : StackPanel
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(IconLabel), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(IconLabel), new PropertyMetadata("", Changed));
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    private readonly TextBlock icon;
    private readonly TextBlock text;
    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        icon = new TextBlock { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        Children.Add(icon); Children.Add(text);
    }
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var content = (IconLabel)owner;
        content.icon.Text = content.Glyph; content.text.Text = content.Label;
        content.text.Visibility = string.IsNullOrEmpty(content.Label) ? Visibility.Collapsed : Visibility.Visible;
        content.icon.Margin = new Thickness(0, 0, string.IsNullOrEmpty(content.Label) ? 0 : 8, 0);
    }
}
internal static class WindowsUi
{
    internal static T Resource<T>(T element, DependencyProperty property, string key) where T : FrameworkElement
    { element.SetResourceReference(property, key); return element; }
    internal static IconLabel Label(string glyph, string label) => new() { Glyph = glyph, Label = label };
    internal static TextBlock Secondary(TextBlock text) => Resource(text, TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
    internal static TextBlock Icon(string glyph, double size = 16) => Resource(new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center }, TextBlock.FontFamilyProperty, "IconFont");
}
internal static class Appearance
{
    internal static string Selected { get; private set; } = "System";
    private sealed record Settings(string Theme);
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenFlow", "settings.json");
    internal static void Initialize()
    {
        string selection = "System";
        try { if (File.Exists(SettingsPath)) selection = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))?.Theme ?? "System"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        Apply(selection, false);
    }
    internal static bool Apply(string selection, bool persist = true)
    {
        Selected = selection is "Light" or "Dark" ? selection : "System";
#pragma warning disable WPF0001 // Isolate .NET's experimental Fluent theme API here.
        Application.Current.ThemeMode = Selected switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
#pragma warning restore WPF0001
        if (!persist) return true;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Settings(Selected)));
            File.Move(temp, SettingsPath, true); return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
