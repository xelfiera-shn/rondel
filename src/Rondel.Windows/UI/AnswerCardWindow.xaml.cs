using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rondel.Core.Flow;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Windows.Interop;
using static Rondel.Windows.Interop.Native;

namespace Rondel.Windows.UI;

/// <summary>What the answer card shows (a <see cref="QuickAnswer"/> at one moment, or made up for a preview).</summary>
internal sealed record AnswerView(string Question, string Text, AnswerState State, string? Tool, string? Error, bool CanContinue);

/// <summary>What the card says about where the question came from: the prompt card's title, icon and picture.</summary>
internal sealed record AnswerHeader(string Title, char Glyph, string? Location, byte[]? Image, string AgentName);

/// <summary>
/// A Quick Conversation's first answer, on a card of its own that floats over the other windows like the agents card:
/// it opens without taking the focus, so the user goes on working while the answer is written, and it stays until it is
/// closed (×, Close, Esc) or the conversation continues in a terminal. Its header drags it. Once clicked, Tab moves
/// between its buttons, Enter runs the selected one, Esc stops an answer being written and otherwise closes the card.
/// Nothing of the answer is kept once it closes.
/// </summary>
public partial class AnswerCardWindow : Window
{
    readonly QuickAnswer? _answer;
    readonly AnswerHeader _header;
    RondelPalette _palette;
    AnswerView _view;
    nint _hwnd;
    RECT _work;
    DispatcherTimer? _render;
    DispatcherTimer? _copied;

    /// <param name="answer">The answer to show and act on; null for a preview, which shows <see cref="Show(AnswerView)"/>.</param>
    internal AnswerCardWindow(QuickAnswer? answer, AnswerHeader header, RondelPalette palette)
    {
        InitializeComponent();
        _answer = answer;
        _header = header;
        _palette = palette;
        _view = answer is null ? new AnswerView("", "", AnswerState.Starting, null, null, false) : View(answer);
        TitleGlyph.Text = header.Glyph.ToString();
        TitleText.Text = header.Title;
        LocationText.Text = header.Location ?? "";
        SetUpShot(header.Image);
        CopyAnswer.Content = Loc.Get("Answer.Copy");
        CloseAnswer.Content = Loc.Get("Answer.Close");
        CloseButton.ToolTip = Loc.Get("Answer.Close");
        ApplyLook(palette);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Not in Alt+Tab or the taskbar, like the agents card; unlike it, a click gives it the keyboard.
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (nint)(GetWindowLongPtr(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW));
        };
        SizeChanged += (_, _) => KeepOnScreen();
        Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInside<Button>(source))
                return;
            DragMove();
        };
        PreviewKeyDown += OnKey;
        CopyAnswer.Click += (_, _) => OnCopy();
        CloseAnswer.Click += (_, _) => Close();
        CloseButton.Click += (_, _) => Close();
        ContinueAnswer.Click += (_, _) => OnContinue();
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

    internal QuickAnswer? Answer => _answer;

    /// <summary>
    /// Opens the card where the prompt card was: <paramref name="card"/> and <paramref name="work"/> in physical pixels,
    /// moved down by <paramref name="offset"/> cards so a second one does not hide the first.
    /// </summary>
    internal void ShowAt(RECT card, RECT work, int offset = 0)
    {
        _work = work;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Show();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pad = (int)Math.Round(Root.Margin.Left * scale);
        int step = (int)Math.Round(28 * scale) * offset;
        SetWindowPos(_hwnd, HWND_TOPMOST, card.Left - pad + step, card.Top - pad + step, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        KeepOnScreen();
    }

    /// <summary>The card grows as the answer is written: it moves up when its bottom would leave the work area.</summary>
    void KeepOnScreen()
    {
        if (_hwnd == 0 || _work.Width <= 0)
            return;
        RECT rect;
        unsafe
        {
            GetWindowRect(_hwnd, &rect);
        }
        int x = Math.Clamp(rect.Left, _work.Left, Math.Max(_work.Left, _work.Right - rect.Width));
        int y = Math.Clamp(rect.Top, _work.Top, Math.Max(_work.Top, _work.Bottom - rect.Height));
        if (x != rect.Left || y != rect.Top)
            SetWindowPos(_hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    internal void ApplyLook(RondelPalette palette)
    {
        _palette = palette;
        Color text = ((SolidColorBrush)palette.Text).Color;
        Resources["ChipText"] = palette.SubText;
        Resources["ChipBorder"] = new SolidColorBrush(Color.FromArgb(0x38, text.R, text.G, text.B));
        Resources["ChipOnFill"] = new SolidColorBrush(Color.FromArgb(0x2E, palette.BrandFrom.R, palette.BrandFrom.G, palette.BrandFrom.B));
        Resources["ChipOnBorder"] = palette.Accent;
        Resources["ChipOnText"] = palette.Text;
        Resources["HoverBrush"] = new SolidColorBrush(Color.FromArgb(0x26, palette.BrandFrom.R, palette.BrandFrom.G, palette.BrandFrom.B));
        Card.Background = palette.Card;
        Card.BorderBrush = palette.Brand;
        CardShadow.Opacity = palette.ShadowOpacity;
        TitleGlyph.Foreground = palette.Brand;
        TitleText.Foreground = palette.Text;
        LocationText.Foreground = palette.SubText;
        CloseGlyph.Foreground = palette.SubText;
        ShotFrame.BorderBrush = palette.TileStroke;
        ShotFrame.Background = palette.InputBackground;
        QuestionFrame.Background = palette.InputBackground;
        QuestionText.Foreground = palette.Text;
        AnswerText.Foreground = palette.Text;
        AnswerText.SelectionBrush = palette.Accent;
        Render();
    }

    /// <summary>Shows <paramref name="view"/> (a preview's made-up answer).</summary>
    internal void Show(AnswerView view)
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
        }, Dispatcher);
        if (!_render.IsEnabled)
            _render.Start();
    }

    static AnswerView View(QuickAnswer answer) =>
        new(answer.Question, answer.Text, answer.State, answer.Tool, answer.Error, answer.CanContinue);

    void Render()
    {
        var view = _view;
        QuestionText.Text = view.Question;
        // Follow the end while it is written, unless the user scrolled up to read.
        bool atEnd = AnswerText.ExtentHeight - AnswerText.ViewportHeight - AnswerText.VerticalOffset < 6;
        var look = new MarkdownLook(_palette.Text, _palette.SubText, _palette.Accent, _palette.InputBackground, _palette.TileStroke,
            Loc.Get("Answer.Copy"), (Style)FindResource("CardButton"), CopyText, OpenLink);
        AnswerText.Document = MarkdownView.Build(view.Text, look);
        AnswerText.Visibility = view.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (atEnd)
            AnswerText.ScrollToEnd();

        AnswerStatus.Foreground = view.State == AnswerState.Failed ? _palette.Error : _palette.SubText;
        AnswerStatus.Text = view.State switch
        {
            AnswerState.Failed => Loc.Format("Answer.Failed", _header.AgentName, view.Error ?? ""),
            AnswerState.Stopped => Loc.Get("Answer.Stopped"),
            AnswerState.Done => "", // the header says that nothing is kept
            _ when view.Tool is { } tool => ToolStatus(tool),
            AnswerState.Starting => Loc.Format("Answer.Starting", _header.AgentName),
            _ => Loc.Get("Answer.Writing"),
        };
        AnswerStatus.Visibility = AnswerStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyAnswer.IsEnabled = view.Text.Length > 0;
        bool failed = view.State == AnswerState.Failed;
        ContinueAnswer.Content = Loc.Get(failed ? "Answer.OpenInTerminal" : "Answer.Continue");
        ContinueAnswer.IsEnabled = failed || view.CanContinue;
    }

    static string ToolStatus(string tool) => tool switch
    {
        "WebSearch" or "web_search" => Loc.Get("Answer.Tool.WebSearch"),
        "WebFetch" or "web_fetch" => Loc.Get("Answer.Tool.WebFetch"),
        "Read" => Loc.Get("Answer.Tool.Read"),
        _ => Loc.Format("Answer.Tool.Other", tool),
    };

    void SetUpShot(byte[]? png)
    {
        if (png is not { Length: > 0 })
            return;
        try
        {
            var picture = new BitmapImage();
            using var stream = new MemoryStream(png);
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.StreamSource = stream;
            picture.EndInit();
            picture.Freeze();
            ShotImage.Source = picture;
            ShotFrame.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
            Log.Warn("Showing the screenshot on the answer card failed", ex);
        }
    }

    void OnCopy()
    {
        CopyText(_view.Text);
        CopyAnswer.Content = Loc.Get("Answer.Copied");
        _copied ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
        {
            _copied!.Stop();
            CopyAnswer.Content = Loc.Get("Answer.Copy");
        }, Dispatcher);
        _copied.Stop();
        _copied.Start();
    }

    static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warn("Copying the answer failed: the clipboard is busy", ex);
        }
    }

    static void OpenLink(Uri uri)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
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
            _answer.OpenInTerminal();
        else
            _answer.Continue();
        if (_answer.IsClosed)
            Close();
    }

    /// <summary>Keys once the card has the keyboard: one key, one job.</summary>
    void OnKey(object sender, KeyEventArgs e)
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
                MoveFocus(back: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case Key.Enter:
                e.Handled = true;
                if (Keyboard.FocusedElement is Button { IsEnabled: true } button)
                    Run(button);
                break;
            case Key.Space when Keyboard.FocusedElement is Button:
                e.Handled = true; // Enter runs a button; Space does nothing here
                break;
        }
    }

    void MoveFocus(bool back)
    {
        var buttons = new[] { CopyAnswer, CloseAnswer, ContinueAnswer }.Where(b => b.IsEnabled).ToList();
        if (buttons.Count == 0)
            return;
        int at = Keyboard.FocusedElement is Button focused ? buttons.IndexOf(focused) : -1;
        int next = at < 0 ? (back ? buttons.Count - 1 : 0) : (at + (back ? buttons.Count - 1 : 1)) % buttons.Count;
        Keyboard.Focus(buttons[next]);
    }

    void Run(Button button)
    {
        if (button == CopyAnswer)
            OnCopy();
        else if (button == CloseAnswer)
            Close();
        else if (button == ContinueAnswer)
            OnContinue();
    }

    static bool IsInside<T>(DependencyObject source) where T : DependencyObject
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is T)
                return true;
        }
        return false;
    }

    /// <summary>Developer aid (--render-answer): the card drawn into a bitmap at 192 DPI, without showing it.</summary>
    internal BitmapSource RenderPreview(Color background)
    {
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = new Size(Root.DesiredSize.Width + 40, Root.DesiredSize.Height + 40);
        Root.Arrange(new Rect(new Point(20, 20), Root.DesiredSize));
        Root.UpdateLayout();
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(size));
        var bitmap = new RenderTargetBitmap((int)(size.Width * 2), (int)(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(backdrop);
        bitmap.Render(Root);
        bitmap.Freeze();
        return bitmap;
    }
}
