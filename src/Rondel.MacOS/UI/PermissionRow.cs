using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Rondel.Core.Localization;

namespace Rondel.MacOS.UI;

/// <summary>
/// A permission Rondel needs: what it is for, whether it is granted, and a button that asks for it while it is not.
/// Settings and the welcome window show the same rows.
/// </summary>
internal sealed class PermissionRow : Grid
{
    readonly Ellipse _dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _state;
    readonly Button _allow;

    public PermissionRow(string title, string description, Action allow, PageLook look)
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = look.Text };
        _allow = new Button { Content = Loc.Get("Settings.Permissions.Allow"), Padding = new Thickness(12, 5), Margin = new Thickness(12, 0, 0, 0) };
        _allow.Click += (_, _) => allow();
        var text = new StackPanel
        {
            Margin = new Thickness(0, 0, 16, 0),
            Children =
            {
                new TextBlock { Text = title, FontSize = 13, Foreground = look.Text },
                new TextBlock { Text = description, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Foreground = look.SubText },
            },
        };
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _dot, _state, _allow },
        };
        Children.Add(text);
        SetColumn(right, 1);
        Children.Add(right);
    }

    /// <param name="granted">Null while it is not known yet.</param>
    public void Show(bool? granted)
    {
        _state.Text = granted is null ? "" : Loc.Get(granted.Value ? "Settings.Permissions.Granted" : "Settings.Permissions.Missing");
        _dot.IsVisible = granted is not null;
        _dot.Fill = granted == true ? new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)) : new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
        _allow.IsVisible = granted == false;
    }
}
