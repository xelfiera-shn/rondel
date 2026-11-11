using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Rondel.Core.Flow;
using Rondel.Core.Localization;
using Rondel.Core.Logging;

namespace Rondel.MacOS.UI;

/// <summary>What the answer card shows (a <see cref="QuickAnswer"/> at one moment, or made up for a preview).</summary>
internal sealed record AnswerView(string Question, string Text, AnswerState State, string? Tool, string? Error, bool CanContinue);

/// <summary>What the card says about where the question came from: the prompt card's title, icon and picture.</summary>
internal sealed record AnswerHeader(string Title, char Glyph, string? Location, byte[]? Image, string AgentName);

/// <summary>
/// A Quick Conversation's first answer, on a card of its own that floats over the other windows, as on Windows: it
/// opens without taking the keyboard, so the user goes on working while the answer is written, and it stays until it is
/// closed (×, Close, Esc) or the conversation continues in a terminal. Its header drags it. Once clicked, Tab moves
/// between its buttons, Return runs the selected one, Esc stops an answer being written and otherwise closes the card.
/// Nothing of the answer is kept once it closes.
/// </summary>
internal sealed class AnswerCardWindow : Window
{
    /// <summary>Room around the card for its shadow.</summary>
    const double Pad = 24;

    readonly QuickAnswer? _answer;
    readonly AnswerHeader _header;
    readonly Border _card = new() { Width = 470, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.2), Padding = new Thickness(16, 12, 12, 12) };
    readonly Rectangle _glyph = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _title = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 360 };
    readonly TextBlock _location = new() { FontSize = 11, Margin = new Thickness(0, 3, 0, 10), TextTrimming = TextTrimming.PathSegmentEllipsis };
    readonly TextBlock _closeGlyph = new() { Text = "✕", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _closeButton = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Top };
    readonly Border _shotFrame = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(1), IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Border _questionFrame = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 0, 0, 10) };
    readonly TextBlock _question = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ScrollViewer _answerScroll = new() { MaxHeight = 400, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock _status = new() { FontSize = 11.5, Margin = new Thickness(1, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly Pill _copy, _close, _continue;
    MenuPalette _palette;
    AnswerView _view;
    PixelRect _work;
    DispatcherTimer? _render;
    DispatcherTimer? _copied;

    /// <param name="answer">The answer to show and act on; null for a preview, which shows <see cref="Show(AnswerView)"/>.</param>
    public AnswerCardWindow(QuickAnswer? answer, AnswerHeader header, MenuPalette palette)
    {
        _answer = answer;
        _header = header;
        _palette = palette;
        _view = answer is null ? new AnswerView("", "", AnswerState.Starting, null, null, false) : View(answer);
        Title = "Rondel";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        _copy = new Pill(Loc.Get("Answer.Copy"), primary: false, palette);
        _close = new Pill(Loc.Get("Answer.Close"), primary: false, palette) { Margin = new Thickness(0, 0, 8, 0) };
        _continue = new Pill("", primary: true, palette);
        _copy.Clicked += OnCopy;
        _close.Clicked += Close;
        _continue.Clicked += OnContinue;
        _closeButton.Child = _closeGlyph;
        _closeButton.PointerEntered += (_, _) =>
            _closeButton.Background = new SolidColorBrush(Color.FromArgb(0x26, _palette.BrandFrom.R, _palette.BrandFrom.G, _palette.BrandFrom.B));
        _closeButton.PointerExited += (_, _) => _closeButton.Background = Brushes.Transparent;
        ToolTip.SetTip(_closeButton, Loc.Get("Answer.Close"));
        _closeButton.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        _questionFrame.Child = _question;

        var mask = SymbolIcons.Mask(SymbolIcons.NameFor(header.Glyph), 16);
        _glyph.OpacityMask = mask is null ? null : new ImageBrush(mask) { Stretch = Stretch.Uniform };
        _title.Text = header.Title;
        _location.Text = header.Location ?? "";
        SetUpShot(header.Image);

        DockPanel.SetDock(_closeButton, Dock.Right);
        var headerPanel = new DockPanel
        {
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Children =
            {
                _closeButton,
                new StackPanel
                {
                    Margin = new Thickness(0, 2, 0, 0),
                    Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { _glyph, _title } }, _location },
                },
            },
        };
        // Dragging the header moves the card.
        headerPanel.PointerPressed += (_, e) =>
        {
            if (!_closeButton.IsPointerOver && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
        DockPanel.SetDock(_copy, Dock.Left);
        DockPanel.SetDock(_continue, Dock.Right);
        DockPanel.SetDock(_close, Dock.Right);
        _card.Child = new StackPanel
        {
            Children =
            {
                headerPanel,
                new StackPanel
                {
                    Margin = new Thickness(0, 0, 4, 0),
                    Children =
                    {
                        _shotFrame, _questionFrame, _answerScroll, _status,
                        new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false, Children = { _copy, _continue, _close } },
                    },
                },
            },
        };
        Content = new Panel { Margin = new Thickness(Pad), Children = { _card } };
        ApplyLook(palette);

        SizeChanged += (_, _) => KeepOnScreen();
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (answer is not null)
        {
            answer.Changed += OnAnswerChanged;
            Closed += (_, _) =>
            {
                answer.Changed -= OnAnswerChanged;
                answer.Close(); // the agent stops and the sweep takes what it kept; nothing once it continued in a terminal
            };
        }
        Render();
    }

    /// <summary>The answer's session went on in a terminal (its id).</summary>
    public event Action<string>? ContinuedInTerminal;

    /// <summary>
    /// Opens the card where the prompt card was: <paramref name="card"/>'s top left and <paramref name="work"/> in screen
    /// pixels, moved down by <paramref name="offset"/> cards so a second one does not hide the first.
    /// </summary>
    public void ShowAt(PixelPoint card, PixelRect work, int offset = 0)
    {
        _work = work;
        // Window positions are in desktop units (points on macOS).
        double scale = DesktopScaling;
        int pad = (int)Math.Round(Pad * scale), step = (int)Math.Round(28 * scale) * offset;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(card.X - pad + step, card.Y - pad + step);
        Show();
        KeepOnScreen();
    }

    /// <summary>The card grows as the answer is written: it moves up when its bottom would leave the work area.</summary>
    void KeepOnScreen()
    {
        if (_work.Width <= 0)
            return;
        var size = PixelSize.FromSize(Bounds.Size, DesktopScaling);
        int x = Math.Clamp(Position.X, _work.X, Math.Max(_work.X, _work.Right - size.Width));
        int y = Math.Clamp(Position.Y, _work.Y, Math.Max(_work.Y, _work.Bottom - size.Height));
        if (x != Position.X || y != Position.Y)
            Position = new PixelPoint(x, y);
    }

    public void ApplyLook(MenuPalette palette)
    {
        _palette = palette;
        _card.Background = palette.Card;
        _card.BorderBrush = palette.Brand;
        _card.Effect = new DropShadowEffect { BlurRadius = 26, OffsetY = 5, Color = Colors.Black, Opacity = palette.ShadowOpacity };
        _glyph.Fill = palette.Brand;
        _title.Foreground = palette.Text;
        _location.Foreground = palette.SubText;
        _closeGlyph.Foreground = palette.SubText;
        _shotFrame.BorderBrush = palette.TileStroke;
        _shotFrame.Background = palette.InputBackground;
        _questionFrame.Background = palette.InputBackground;
        _question.Foreground = palette.Text;
        foreach (var pill in new[] { _copy, _close, _continue })
            pill.ApplyPalette(palette);
        Render();
    }

    /// <summary>Shows <paramref name="view"/> (a preview's made-up answer).</summary>
    public void Show(AnswerView view)
    {
        _view = view;
        Render();
    }

    /// <summary>Pieces arrive many times a second: the card draws at most every 60 ms.</summary>
    void OnAnswerChanged()
    {
        _render ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) =>
        {
            _render!.Stop();
            if (_answer is not null)
                Show(View(_answer));
        });
        if (!_render.IsEnabled)
            _render.Start();
    }

    static AnswerView View(QuickAnswer answer) =>
        new(answer.Question, answer.Text, answer.State, answer.Tool, answer.Error, answer.CanContinue);

    void Render()
    {
        var view = _view;
        _question.Text = view.Question;
        // Follow the end while it is written, unless the user scrolled up to read.
        bool atEnd = _answerScroll.Extent.Height - _answerScroll.Viewport.Height - _answerScroll.Offset.Y < 6;
        var look = new MarkdownLook(_palette.Text, _palette.SubText, _palette.Accent, _palette.InputBackground, _palette.TileStroke,
            Loc.Get("Answer.Copy"), CodeCopyButton, CopyText, OpenLink);
        _answerScroll.Content = MarkdownView.Build(view.Text, look);
        _answerScroll.IsVisible = view.Text.Length > 0;
        if (atEnd)
            Dispatcher.UIThread.Post(_answerScroll.ScrollToEnd, DispatcherPriority.Background);

        _status.Foreground = view.State == AnswerState.Failed ? _palette.Error : _palette.SubText;
        _status.Text = view.State switch
        {
            AnswerState.Failed => Loc.Format("Answer.Failed", _header.AgentName, view.Error ?? ""),
            AnswerState.Stopped => Loc.Get("Answer.Stopped"),
            AnswerState.Done => "", // the header says that nothing is kept
            _ when view.Tool is { } tool => ToolStatus(tool),
            AnswerState.Starting => Loc.Format("Answer.Starting", _header.AgentName),
            _ => Loc.Get("Answer.Writing"),
        };
        _status.IsVisible = _status.Text.Length > 0;
        _copy.IsEnabled = view.Text.Length > 0;
        bool failed = view.State == AnswerState.Failed;
        _continue.Text = Loc.Get(failed ? "Answer.OpenInTerminal" : "Answer.Continue");
        _continue.IsEnabled = failed || view.CanContinue;
    }

    static string ToolStatus(string tool) => tool switch
    {
        "WebSearch" or "web_search" => Loc.Get("Answer.Tool.WebSearch"),
        "WebFetch" or "web_fetch" => Loc.Get("Answer.Tool.WebFetch"),
        "Read" => Loc.Get("Answer.Tool.Read"),
        _ => Loc.Format("Answer.Tool.Other", tool),
    };

    Control CodeCopyButton(string label, Action copy)
    {
        var pill = new Pill(label, primary: false, _palette) { Focusable = false, FontSize = 11, Padding = new Thickness(9, 1, 9, 2) };
        pill.Clicked += copy;
        return pill;
    }

    void SetUpShot(byte[]? png)
    {
        if (png is not { Length: > 0 })
            return;
        try
        {
            using var stream = new MemoryStream(png);
            _shotFrame.Child = new Image { Source = new Bitmap(stream), Stretch = Stretch.Uniform, MaxWidth = 436, MaxHeight = 120, HorizontalAlignment = HorizontalAlignment.Left };
            _shotFrame.IsVisible = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            Log.Warn("Showing the screenshot on the answer card failed", ex);
        }
    }

    void OnCopy()
    {
        CopyText(_view.Text);
        _copy.Text = Loc.Get("Answer.Copied");
        _copied ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
        {
            _copied!.Stop();
            _copy.Text = Loc.Get("Answer.Copy");
        });
        _copied.Stop();
        _copied.Start();
    }

    void CopyText(string text)
    {
        if (Clipboard is { } clipboard)
            _ = clipboard.SetTextAsync(text);
    }

    static void OpenLink(Uri uri)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            start.ArgumentList.Add(uri.AbsoluteUri);
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Opening {uri} failed", ex);
        }
    }

    void OnContinue()
    {
        if (_answer is null)
            return;
        if (_answer.State == AnswerState.Failed)
        {
            _answer.OpenInTerminal();
        }
        else
        {
            _answer.Continue();
            if (_answer.IsClosed && _answer.SessionId is { } session)
                ContinuedInTerminal?.Invoke(session);
        }
        if (_answer.IsClosed)
            Close();
    }

    /// <summary>Keys once the card has the keyboard: one key, one job.</summary>
    void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_answer is { IsRunning: true })
                    _answer.Stop();
                else
                    Close();
                break;
            case Key.Tab:
                e.Handled = true;
                MoveFocus(back: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                break;
            case Key.Enter:
                if (FocusManager?.GetFocusedElement() is Pill { IsEnabled: true } pill)
                {
                    e.Handled = true;
                    pill.Run();
                }
                break;
        }
    }

    void MoveFocus(bool back)
    {
        var pills = new[] { _copy, _close, _continue }.Where(p => p.IsEnabled).ToList();
        if (pills.Count == 0)
            return;
        int at = FocusManager?.GetFocusedElement() is Pill focused ? pills.IndexOf(focused) : -1;
        int next = at < 0 ? (back ? pills.Count - 1 : 0) : (at + (back ? pills.Count - 1 : 1)) % pills.Count;
        pills[next].Focus(NavigationMethod.Tab);
    }

    /// <summary>Developer aid (--render-answer): the card drawn into a bitmap at 2x, without showing it.</summary>
    public RenderTargetBitmap RenderPreview(Color background)
    {
        // Drawn from a panel inside the window, at a fixed size as the other pictures are, then cut to the card's height.
        const double Width = 510, Height = 1400;
        var content = (Panel)Content!;
        Content = null;
        content.Margin = new Thickness(20);
        content.VerticalAlignment = VerticalAlignment.Top;
        _card.Effect = null; // the render target draws what a shadowed card holds at twice its size
        var root = new Panel { Width = Width, Height = Height, Background = new SolidColorBrush(background), Children = { content } };
        Content = root;
        SizeToContent = SizeToContent.Manual;
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Measure(new Size(Width, Height));
            Arrange(new Rect(0, 0, Width, Height));
        }
        double height = Math.Ceiling(content.Bounds.Bottom + 20);
        var bitmap = new RenderTargetBitmap(new PixelSize((int)(Width * 2), (int)(height * 2)), new Vector(192, 192));
        bitmap.Render(root);
        return bitmap;
    }

    /// <summary>A button under the answer: a pill like the prompt card's chips, ringed when it has the keyboard.</summary>
    sealed class Pill : Border
    {
        readonly TextBlock _text = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly bool _primary;
        MenuPalette _palette;

        public Pill(string text, bool primary, MenuPalette palette)
        {
            _primary = primary;
            _palette = palette;
            _text.Text = text;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            CornerRadius = new CornerRadius(13);
            Padding = new Thickness(13, 5, 13, 6);
            Child = _text;
            PointerEntered += (_, _) => Paint();
            PointerExited += (_, _) => Paint();
            GotFocus += (_, _) => Paint();
            LostFocus += (_, _) => Paint();
            PointerReleased += (_, e) =>
            {
                e.Handled = true;
                Run();
            };
            PropertyChanged += (_, e) =>
            {
                if (e.Property == IsEnabledProperty)
                {
                    Opacity = IsEnabled ? 1 : 0.4;
                    Cursor = new Cursor(IsEnabled ? StandardCursorType.Hand : StandardCursorType.Arrow);
                }
            };
            Paint();
        }

        public event Action? Clicked;

        public string Text
        {
            get => _text.Text ?? "";
            set => _text.Text = value;
        }

        public double FontSize
        {
            get => _text.FontSize;
            set => _text.FontSize = value;
        }

        public void Run()
        {
            if (IsEnabled)
                Clicked?.Invoke();
        }

        public void ApplyPalette(MenuPalette palette)
        {
            _palette = palette;
            Paint();
        }

        void Paint()
        {
            var text = ((ISolidColorBrush)_palette.Text).Color;
            bool lit = _primary || IsPointerOver || IsFocused;
            Background = _primary ? new SolidColorBrush(Color.FromArgb(0x2E, _palette.BrandFrom.R, _palette.BrandFrom.G, _palette.BrandFrom.B)) : Brushes.Transparent;
            BorderBrush = lit ? _palette.Accent : new SolidColorBrush(Color.FromArgb(0x38, text.R, text.G, text.B));
            BorderThickness = new Thickness(IsFocused ? 1.5 : 1);
            _text.Foreground = _primary ? _palette.Text : _palette.SubText;
        }
    }
}
