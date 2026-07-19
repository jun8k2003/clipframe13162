using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipFrame.UI;

/// <summary>Minimal single-line text prompt (WPF has no built-in InputBox).</summary>
internal static class InputDialog
{
    /// <summary>Shows a modal prompt. Returns the entered text, or null if cancelled.</summary>
    public static string? Prompt(Window? owner, string title, string message, string defaultText = "")
    {
        var win = new Window
        {
            Title = title,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Owner = owner,
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)),
        };

        var root = new StackPanel { Margin = new Thickness(14) };

        root.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });

        var box = new TextBox { Text = defaultText, FontSize = 13, Padding = new Thickness(4, 3, 4, 3) };
        root.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        string? result = null;
        var ok = new Button { Content = "OK", Width = 76, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 88, IsCancel = true };

        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };
        cancel.Click += (_, _) => { win.DialogResult = false; };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        win.Content = root;
        win.Loaded += (_, _) => { box.SelectAll(); box.Focus(); };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { result = box.Text; win.DialogResult = true; } };

        return win.ShowDialog() == true ? result : null;
    }
}
