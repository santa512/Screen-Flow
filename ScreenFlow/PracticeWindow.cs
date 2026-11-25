using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenFlow;
public sealed class PracticeWindow : Window
{
    internal TextBox PracticeInput { get; }
    internal TextBox AddressInput { get; }
    internal Button CompleteButton { get; }
    internal TextBlock ResultLabel { get; }
    internal int NewTabsOpened { get; private set; }
    public PracticeWindow()
    {
        Title = "ScreenFlow Practice"; Width = 600; Height = 580; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(IconProperty, "AppLogo");
        var panel = new StackPanel { Margin = new Thickness(32) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "A place to practice", FontSize = 27, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(WindowsUi.Secondary(new TextBlock { Text = "Try a visual click, type some text, then click the button.\nAll activity stays inside this window.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 23) }));
        panel.Children.Add(WindowsUi.Secondary(new TextBlock { Text = "Practice address bar · Ctrl+L to focus", FontSize = 12, Margin = new Thickness(0, 0, 0, 7) }));
        AddressInput = new TextBox { Text = "https://example.invalid/practice", Margin = new Thickness(0, 0, 0, 20) };
        panel.Children.Add(AddressInput);
        panel.Children.Add(new TextBlock { Text = "Your text", Margin = new Thickness(0, 0, 0, 7) });
        var input = new TextBox { Name = "PracticeText", Text = "Click here to type", MinHeight = 75, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        PracticeInput = input;
        System.Windows.Automation.AutomationProperties.SetName(input, "Practice text field");
        panel.Children.Add(input);
        var result = WindowsUi.Secondary(new TextBlock { Text = "Waiting for your first run…", Margin = new Thickness(0, 20, 0, 0), TextWrapping = TextWrapping.Wrap });
        ResultLabel = result;
        var button = new Button { Content = WindowsUi.Label("\uE73E", "Complete practice"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0) };
        button.SetResourceReference(StyleProperty, "AccentButtonStyle");
        System.Windows.Automation.AutomationProperties.SetName(button, "Complete practice");
        CompleteButton = button;
        button.Click += (_, _) => { result.Text = "Success! Received: " + input.Text; result.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush"); };
        panel.Children.Add(button); panel.Children.Add(result);
        PreviewKeyDown += (_, e) => {
            if (e.Key == System.Windows.Input.Key.T && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) {
                NewTabsOpened++; result.Text = "New tab shortcut received: Ctrl+T"; e.Handled = true;
            } else if (e.Key == System.Windows.Input.Key.L && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) {
                AddressInput.Focus(); AddressInput.SelectAll(); e.Handled = true;
            } else if (e.Key == System.Windows.Input.Key.Escape) { PracticeInput.Focus(); e.Handled = true; }
        };
    }
}
