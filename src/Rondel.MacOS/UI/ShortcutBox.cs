using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Rondel.Core.Localization;
using Rondel.MacOS.Trigger;

namespace Rondel.MacOS.UI;

/// <summary>
/// Records a keyboard shortcut, the Mac way: click the box, press the combination, and it reads ⌃⌥R. Esc stops
/// recording without a change, and the ✕ clears the shortcut. A plain key, or one macOS keeps for itself, is not taken,
/// and the line under the box says why. While it records, Rondel's own shortcuts are let go.
/// </summary>
internal sealed class ShortcutBox : StackPanel
{
    readonly PageLook _look;
    readonly Action<string> _save;
    readonly Action<bool> _recording;
    readonly Border _box;
    readonly TextBlock _text = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _status = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
    string _current = "";
    bool _isRecording;

    /// <param name="save">Stores the new shortcut ("" for none).</param>
    /// <param name="recording">True while it records, false after.</param>
    public ShortcutBox(PageLook look, Action<string> save, Action<bool> recording)
    {
        _look = look;
        _save = save;
        _recording = recording;
        _box = new Border
        {
            Width = 170, Height = 30, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Focusable = true, Cursor = new Cursor(StandardCursorType.Hand), Child = _text,
        };
        var clear = new Button { Content = "✕", Padding = new Thickness(9, 4), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(clear, Loc.Get("Settings.Hotkey.Clear"));
        clear.Click += (_, _) =>
        {
            StopRecording();
            ShowStatus(null);
            _save("");
        };
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _box, clear } });
        Children.Add(_status);
        _box.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _box.Focus();
            StartRecording();
        };
        _box.LostFocus += (_, _) => StopRecording();
        _box.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Paint();
    }

    /// <summary>Shows the stored shortcut and, when it is not active, why.</summary>
    public void Show(string shortcut, string? error)
    {
        _current = shortcut;
        if (!_isRecording)
            Paint();
        ShowStatus(error);
    }

    void StartRecording()
    {
        if (_isRecording)
            return;
        _isRecording = true;
        _recording(true);
        ShowStatus(null);
        Paint();
    }

    void StopRecording()
    {
        if (!_isRecording)
            return;
        _isRecording = false;
        _recording(false);
        Paint();
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecording)
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                StartRecording();
            }
            return;
        }
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            StopRecording();
            return;
        }
        if (!Hotkey.IsShortcutKey(e.PhysicalKey))
        {
            // A modifier on its own: show what is held so far.
            _text.Text = Hotkey.Symbols(e.KeyModifiers) is { Length: > 0 } held ? held : Loc.Get("Settings.Hotkey.Recording.Mac");
            return;
        }
        var hotkey = new Hotkey(e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta), e.PhysicalKey);
        if (!hotkey.IsUsable)
        {
            ShowStatus(Loc.Get("Settings.Hotkey.NeedsModifier.Mac"));
            return;
        }
        if (hotkey.IsReserved)
        {
            ShowStatus(Loc.Get("Settings.Hotkey.Reserved.Mac"));
            return;
        }
        _current = hotkey.ToString();
        StopRecording();
        _save(_current);
    }

    void Paint()
    {
        _box.Background = _look.Card;
        _box.BorderBrush = _isRecording ? _look.Accent : _look.CardStroke;
        _box.BorderThickness = new Thickness(_isRecording ? 1.5 : 1);
        _text.Text = _isRecording ? Loc.Get("Settings.Hotkey.Recording.Mac")
            : _current.Length == 0 ? Loc.Get("Settings.Hotkey.Example.Mac")
            : Hotkey.Display(_current);
        _text.Foreground = _isRecording || _current.Length == 0 ? _look.SubText : _look.Text;
    }

    void ShowStatus(string? text)
    {
        _status.Text = text ?? "";
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        _status.IsVisible = text is not null;
    }
}
