using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;
using Rondel.Windows.Detection;
using Rondel.Windows.Interop;
using static Rondel.Windows.Interop.Native;
using Path = System.Windows.Shapes.Path;

namespace Rondel.Windows.UI;

/// <summary>
/// Borderless, per-pixel transparent, topmost pie menu. Created once at startup and only
/// moved/shown afterwards so it appears without delay. All geometry is in DIPs around the ring
/// center; screen positions are physical pixels of the monitor under the cursor.
/// </summary>
public partial class RadialMenuWindow : Window
{
    const double OuterR = 164;     // ring radius (MenuRoot in the XAML is 2 × this)
    const double CenterR = 58;     // center disc radius
    const double DeadZoneR = 60;   // closer than this to the center selects nothing
    const double LabelR = 110;     // where icon + label blocks are centered (middle of the ring)
    const double MinLabelWidth = 56;
    const double ShadowPad = 32;   // shadow extent kept on-screen when clamping to the work area
    const int MaxPromptLength = 8000; // keeps the encoded command line well under CreateProcess's limit

    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    sealed record Slice(Path Highlight, FrameworkElement Content, TextBlock Glyph, TextBlock Label, TextBlock Hint)
    {
        public bool Dimmed { get; set; }
    }

    readonly List<Slice> _slices = [];
    nint _hwnd;
    MenuModel? _model;
    RadialPalette _palette = RadialPalette.From(SystemTheme.Read());
    double _span = 360, _offset;
    int _hover = -1;
    bool _isOpen;
    int _version;
    bool _pressedInside;
    RECT _bounds;
    double _centerX, _centerY, _scale = 1;
    nint _previousForeground;
    InputSpec? _input;
    bool _promptStage;
    string _acceptedName = "";

    public RadialMenuWindow()
    {
        InitializeComponent();
        ApplyPalette();
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => { if (_isOpen) SetHover(-1); };
        MouseLeftButtonDown += (_, _) => _pressedInside = true;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => CloseMenu(restoreFocus: false);
        DpiChanged += (_, _) => { if (_isOpen) ApplyBounds(); };
        NameBox.TextChanged += (_, _) => { if (_input is not null && !_promptStage) UpdateNameFeedback(); };
    }

    /// <summary>A slice was chosen (click, release over it, number key, Enter).</summary>
    internal event Action<MenuModel, MenuItemModel>? ItemInvoked;

    internal event Action? MenuClosed;

    internal nint Handle => _hwnd;

    internal bool IsMenuOpen => _isOpen;

    /// <summary>Creates the window handle and renders once off-screen so the first real open is instant.</summary>
    internal void Prepare()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, GetWindowLongPtr(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        var warmup = AppSettings.DefaultActions().Take(4)
            .Select(a => new MenuItemModel(a, BuiltInAgents.ClaudeCode(), a.Id, a.Glyph, "warm-up", true)).ToList();
        BuildSlices(new MenuModel(TriggerContext.None("warm-up"), "warm-up", "warm-up", warmup));
        Surface.Opacity = 0;
        SetWindowPos(_hwnd, 0, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Show();
        Dispatcher.BeginInvoke(() => { if (!_isOpen) Hide(); }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Opens the menu centered on <paramref name="anchor"/> (physical screen pixels).</summary>
    internal void ShowMenu(MenuModel model, POINT anchor)
    {
        _version++;
        _model = model;
        _previousForeground = GetForegroundWindow();
        _pressedInside = false;
        ResetInputMode();
        BuildSlices(model);
        CenterTitle.Text = model.Title;
        CenterSubtitle.Text = model.Subtitle ?? "";
        CenterSubtitle.Visibility = string.IsNullOrEmpty(model.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        // Center on the cursor, but keep the ring and its shadow inside the monitor's work area.
        var (work, dpi) = GetMonitorWorkArea(anchor);
        _scale = dpi / 96.0;
        int size = (int)Math.Round(Width * _scale);
        double half = (OuterR + ShadowPad) * _scale;
        int cx = ClampCenter(anchor.X, work.Left + half, work.Right - half);
        int cy = ClampCenter(anchor.Y, work.Top + half, work.Bottom - half);
        _bounds = new RECT { Left = cx - size / 2, Top = cy - size / 2 };
        _bounds.Right = _bounds.Left + size;
        _bounds.Bottom = _bounds.Top + size;
        _centerX = _bounds.Left + size / 2.0;
        _centerY = _bounds.Top + size / 2.0;

        Surface.BeginAnimation(OpacityProperty, null);
        Surface.Opacity = 0;
        ApplyBounds();
        if (!IsVisible)
            Show();
        ApplyBounds(); // WPF may have rescaled the window while it moved to a monitor with another DPI
        _isOpen = true;

        TakeFocus();
        AnimateOpen();
    }

    /// <summary>The button that opened the menu was released at <paramref name="point"/>.</summary>
    internal void OnHeldRelease(POINT point)
    {
        if (!_isOpen || _input is not null)
            return;
        int index = HitTest((point.X - _centerX) / _scale, (point.Y - _centerY) / _scale);
        if (index >= 0)
            Invoke(index);
    }

    internal void CloseMenu(bool restoreFocus)
    {
        if (!_isOpen)
            return;
        _isOpen = false;
        int version = ++_version;
        SetHover(-1);
        MenuClosed?.Invoke();

        if (restoreFocus && _previousForeground != 0 && _previousForeground != _hwnd
            && GetForegroundWindow() == _hwnd && IsWindow(_previousForeground))
        {
            SetForegroundWindow(_previousForeground);
        }

        var duration = AnimationTime(110);
        var fade = new DoubleAnimation(0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            if (_version != version)
                return;
            Hide();
            ResetInputMode();
        };
        Surface.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.94, duration);
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    internal void UpdateTheme(ThemeColors theme)
    {
        _palette = RadialPalette.From(theme);
        ApplyPalette();
    }

    // ---- placement, focus, animation ----

    void ApplyBounds() =>
        SetWindowPos(_hwnd, HWND_TOPMOST, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, SWP_NOACTIVATE);

    void TakeFocus()
    {
        // Keyboard selection (1..N, Esc) needs focus. Explorer keeps its selection while inactive,
        // and focus goes back to it when the menu closes.
        InputInjector.Nudge();
        if (!SetForegroundWindow(_hwnd))
            Log.Debug("SetForegroundWindow refused; the menu works with the mouse only this time");
        MenuRoot.Focus();
    }

    void AnimateOpen()
    {
        var duration = AnimationTime(150);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        var grow = new DoubleAnimation(SystemParameters.ClientAreaAnimation ? 0.86 : 1, 1, duration) { EasingFunction = ease };
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>Respects "Animation effects" being turned off in Windows.</summary>
    static Duration AnimationTime(int milliseconds) =>
        TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? milliseconds : 0);

    static int ClampCenter(int value, double min, double max) =>
        min > max ? (int)Math.Round((min + max) / 2) : (int)Math.Round(Math.Clamp(value, min, max));

    // ---- input ----

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isOpen || _input is not null)
            return;
        var p = e.GetPosition(MenuRoot);
        SetHover(HitTest(p.X - OuterR, p.Y - OuterR));
    }

    void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool pressed = _pressedInside;
        _pressedInside = false;
        if (!_isOpen || !pressed || _input is not null)
            return;
        var p = e.GetPosition(MenuRoot);
        int index = HitTest(p.X - OuterR, p.Y - OuterR);
        if (index >= 0)
            Invoke(index);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isOpen && _input is not null)
        {
            OnInputKeyDown(e);
            return;
        }
        if (!_isOpen || _slices.Count == 0)
            return;
        int n = _slices.Count;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int number = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => -1,
        };
        if (number >= 0)
        {
            if (number < n)
                Invoke(number);
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case Key.Escape:
                CloseMenu(restoreFocus: true);
                e.Handled = true;
                break;
            case Key.Enter or Key.Space:
                if (_hover >= 0)
                    Invoke(_hover);
                e.Handled = true;
                break;
            case Key.Right or Key.Down or Key.Tab:
                SetHover(_hover < 0 ? 0 : (_hover + 1) % n);
                e.Handled = true;
                break;
            case Key.Left or Key.Up:
                SetHover(_hover < 0 ? n - 1 : (_hover - 1 + n) % n);
                e.Handled = true;
                break;
        }
    }

    void Invoke(int index)
    {
        if (_model is null || index < 0 || index >= _model.Items.Count)
            return;
        SetHover(index);
        ItemInvoked?.Invoke(_model, _model.Items[index]);
    }

    /// <summary>Slice index for an offset from the ring center (DIPs), or -1.</summary>
    int HitTest(double dx, double dy)
    {
        int n = _slices.Count;
        if (n == 0)
            return -1;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < DeadZoneR || distance > OuterR + 1)
            return -1;
        double angle = Math.Atan2(dx, -dy) * 180 / Math.PI; // 0° = up, clockwise
        double relative = ((angle - _offset + _span / 2) % 360 + 360) % 360;
        return Math.Min((int)(relative / _span), n - 1);
    }

    /// <summary>
    /// Late session hints (computed off the UI thread after the menu opened). Ignored if the menu
    /// has moved on to another context meanwhile.
    /// </summary>
    internal void ApplyHints(MenuModel model, IReadOnlyList<(string? Hint, bool Dimmed)> hints)
    {
        if (!_isOpen || !ReferenceEquals(model, _model) || hints.Count != _slices.Count)
            return;
        for (int i = 0; i < hints.Count; i++)
        {
            var slice = _slices[i];
            if (hints[i].Hint is not { } text)
                continue;
            slice.Hint.Text = text;
            slice.Hint.Visibility = Visibility.Visible;
            slice.Dimmed = hints[i].Dimmed;
            Paint(slice, i == _hover);
        }
    }

    // ---- inline input ----

    /// <summary>
    /// Replaces the ring with a card in the menu center that asks for a folder name and, for
    /// "New + Prompt", then a prompt. Validation is shown inline as the user types.
    /// </summary>
    internal void StartInput(InputSpec spec)
    {
        if (!_isOpen)
            return;
        _input = spec;
        _promptStage = false;
        SetHover(-1);
        InputGlyph.Text = spec.Glyph.ToString();
        NameBox.Tag = Loc.Get("Input.NamePlaceholder");
        PromptBox.Tag = Loc.Format("Input.PromptPlaceholder", spec.AgentName);
        NameBox.Text = "";
        PromptBox.Text = "";

        MenuRoot.IsHitTestVisible = false;
        InputCard.Visibility = Visibility.Visible;
        var duration = AnimationTime(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        MenuRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, duration));
        InputCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        var grow = new DoubleAnimation(SystemParameters.ClientAreaAnimation ? 0.9 : 1, 1, duration) { EasingFunction = ease };
        InputScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        InputScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        TakeFocus();
        ShowNameStage();
    }

    void ResetInputMode()
    {
        _input = null;
        _promptStage = false;
        InputCard.BeginAnimation(OpacityProperty, null);
        InputCard.Visibility = Visibility.Collapsed;
        MenuRoot.BeginAnimation(OpacityProperty, null);
        MenuRoot.Opacity = 1;
        MenuRoot.IsHitTestVisible = true;
    }

    void ShowNameStage()
    {
        _promptStage = false;
        InputTitle.Text = _input!.Title;
        PromptBox.Visibility = Visibility.Collapsed;
        NameBox.Visibility = Visibility.Visible;
        AlignPlaceholder(NameBox);
        UpdateNameFeedback();
        Keyboard.Focus(NameBox);
        NameBox.SelectAll();
    }

    void ShowPromptStage(string name)
    {
        _promptStage = true;
        _acceptedName = name;
        InputTitle.Text = $"{_input!.Title} · {name}";
        InputLocation.Text = System.IO.Path.Combine(_input.ParentDir, name);
        NameBox.Visibility = Visibility.Collapsed;
        PromptBox.Visibility = Visibility.Visible;
        AlignPlaceholder(PromptBox);
        SetMessage(Loc.Get("Input.HintPrompt"), isError: false);
        Keyboard.Focus(PromptBox);
        PromptBox.CaretIndex = PromptBox.Text.Length;
    }

    /// <summary>
    /// Puts the placeholder exactly where typed text and the caret start. TextBox keeps its inner
    /// text offset to itself, so the empty box is asked for its caret rectangle instead of guessing.
    /// </summary>
    static void AlignPlaceholder(TextBox box)
    {
        box.ApplyTemplate();
        box.UpdateLayout();
        if (box.Template?.FindName("Placeholder", box) is not TextBlock placeholder)
            return;
        var caret = box.GetRectFromCharacterIndex(0);
        if (caret.IsEmpty || double.IsNaN(caret.Left) || double.IsInfinity(caret.Left))
            return;
        // The template's Grid sits inside the border, so its origin is offset by the border thickness.
        double left = caret.Left - box.BorderThickness.Left;
        placeholder.Margin = new Thickness(left, caret.Top - box.BorderThickness.Top, left, 0);
    }

    void UpdateNameFeedback()
    {
        var input = _input!;
        string name = NameBox.Text;
        InputLocation.Text = name.Length == 0 ? input.ParentDir : System.IO.Path.Combine(input.ParentDir, name);
        string? error = name.Length == 0 ? null : input.ValidateName(name);
        SetMessage(error ?? Loc.Get(input.AskPrompt ? "Input.HintNameNext" : "Input.HintName"), error is not null);
    }

    void SetMessage(string text, bool isError)
    {
        InputMessage.Text = text;
        InputMessage.Foreground = isError ? _palette.Error : _palette.SubText;
    }

    void OnInputKeyDown(KeyEventArgs e)
    {
        var input = _input!;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_promptStage)
                ShowNameStage();
            else
                CloseMenu(restoreFocus: true);
            return;
        }
        if (e.Key != Key.Enter)
            return;

        if (!_promptStage)
        {
            e.Handled = true;
            string name = NameBox.Text;
            if (input.ValidateName(name) is { } error)
                SetMessage(error, isError: true);
            else if (input.AskPrompt)
                ShowPromptStage(name);
            else
                Submit(name, null);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Plain Enter adds a line to the prompt; Ctrl+Enter submits.
            e.Handled = true;
            string prompt = PromptBox.Text.Trim();
            if (prompt.Length == 0)
                SetMessage(Loc.Get("Input.PromptEmpty"), isError: true);
            else if (prompt.Length > MaxPromptLength)
                SetMessage(Loc.Format("Input.PromptTooLong", prompt.Length, MaxPromptLength), isError: true);
            else
                Submit(_acceptedName, prompt);
        }
    }

    void Submit(string name, string? prompt)
    {
        if (_input?.Submit(name, prompt) is { } error)
            SetMessage(error, isError: true);
    }

    // ---- drawing ----

    void BuildSlices(MenuModel model)
    {
        SliceLayer.Children.Clear();
        _slices.Clear();
        _hover = -1;
        int n = model.Items.Count;
        if (n == 0)
            return;

        _span = 360.0 / n;
        _offset = n == 2 ? -90 : 0; // two items read better left/right than top/bottom
        double maxLabelWidth = n <= 4 ? 104 : Math.Max(MinLabelWidth, 2 * LabelR * Math.Sin(Math.PI / n) - 8);
        // Session hints arrive after the menu opened; reserve their line so labels don't move then.
        string hintSample = Loc.Format("Hint.Last", Loc.Format("Time.Minutes", 59));

        if (n > 1)
        {
            for (int i = 0; i < n; i++)
            {
                double angle = _offset + i * _span - _span / 2;
                var from = OnCircle(CenterR + 1, angle);
                var to = OnCircle(OuterR - 1, angle);
                SliceLayer.Children.Add(new Line
                {
                    X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y,
                    Stroke = _palette.Separator,
                    StrokeThickness = 1,
                });
            }
        }

        for (int i = 0; i < n; i++)
        {
            var item = model.Items[i];
            double mid = _offset + i * _span;

            var highlight = new Path
            {
                Data = Sector(CenterR + 4, OuterR - 4, mid - _span / 2 + 1.2, mid + _span / 2 - 1.2),
            };
            SliceLayer.Children.Add(highlight);

            var glyph = new TextBlock
            {
                FontFamily = IconFont,
                FontSize = 20,
                Text = item.Glyph.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
            };
            var label = new TextBlock
            {
                FontSize = 12,
                Text = item.Label,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 34,
            };
            bool expectsHint = item.Action.Hint != SessionHint.None;
            var hint = new TextBlock
            {
                FontSize = 10,
                Text = item.Hint ?? (expectsHint ? hintSample : ""),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
                Visibility = item.Hint is not null ? Visibility.Visible
                    : expectsHint ? Visibility.Hidden
                    : Visibility.Collapsed,
            };
            var stack = new StackPanel();
            stack.Children.Add(glyph);
            stack.Children.Add(label);
            stack.Children.Add(hint);
            SliceLayer.Children.Add(stack);
            PlaceLabel(stack, mid, maxLabelWidth);

            var slice = new Slice(highlight, stack, glyph, label, hint) { Dimmed = item.Dimmed };
            _slices.Add(slice);
            Paint(slice, hot: false);
        }
    }

    /// <summary>
    /// Centers a slice's icon + label on its middle angle, using the widest width (≤ max) at which the
    /// measured block stays inside the ring and its own wedge. Narrower widths wrap the label onto a
    /// second line, which matters for the left/right slices where the ring is only ~100 DIP deep.
    /// </summary>
    void PlaceLabel(StackPanel block, double midDegrees, double maxWidth)
    {
        Size size = default;
        for (double width = maxWidth; width >= MinLabelWidth; width -= 2)
        {
            block.Width = width;
            block.Measure(new Size(width, double.PositiveInfinity));
            size = new Size(width, block.DesiredSize.Height);
            if (FitsInSlice(midDegrees, size))
                break;
        }
        var center = OnCircle(LabelR, midDegrees);
        Canvas.SetLeft(block, center.X - size.Width / 2);
        Canvas.SetTop(block, center.Y - size.Height / 2);
    }

    bool FitsInSlice(double midDegrees, Size size)
    {
        var center = OnCircle(LabelR, midDegrees);
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            double dx = center.X + sx * size.Width / 2 - OuterR;
            double dy = center.Y + sy * size.Height / 2 - OuterR;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            if (radius < CenterR + 5 || radius > OuterR - 7)
                return false;
            if (_span < 360)
            {
                double angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
                double fromMiddle = ((angle - midDegrees) % 360 + 540) % 360 - 180;
                if (Math.Abs(fromMiddle) > _span / 2 - 3)
                    return false;
            }
        }
        return true;
    }

    void SetHover(int index)
    {
        if (index == _hover)
            return;
        if (_hover >= 0 && _hover < _slices.Count)
            Paint(_slices[_hover], hot: false);
        _hover = index;
        if (_hover >= 0 && _hover < _slices.Count)
            Paint(_slices[_hover], hot: true);
    }

    void Paint(Slice slice, bool hot)
    {
        slice.Highlight.Fill = hot ? _palette.Hover : null;
        slice.Glyph.Foreground = hot ? _palette.HoverText : _palette.Text;
        slice.Label.Foreground = hot ? _palette.HoverText : _palette.Text;
        slice.Hint.Foreground = hot ? _palette.HoverText : _palette.SubText;
        slice.Content.Opacity = slice.Dimmed && !hot ? 0.45 : 1;
    }

    void ApplyPalette()
    {
        RingBase.Fill = _palette.Ring;
        RingBase.Stroke = _palette.RingStroke;
        RingShadow.Opacity = _palette.ShadowOpacity;
        CenterDisc.Fill = _palette.Center;
        CenterDisc.Stroke = _palette.CenterStroke;
        CenterGlyph.Foreground = _palette.Text;
        CenterTitle.Foreground = _palette.Text;
        CenterSubtitle.Foreground = _palette.SubText;
        InputCard.Background = _palette.Card;
        InputCard.BorderBrush = _palette.CardStroke;
        CardShadow.Opacity = _palette.ShadowOpacity;
        InputGlyph.Foreground = _palette.Text;
        InputTitle.Foreground = _palette.Text;
        InputLocation.Foreground = _palette.SubText;
        foreach (var box in new[] { NameBox, PromptBox })
        {
            box.Background = _palette.InputBackground;
            box.BorderBrush = _palette.Hover;
            box.Foreground = _palette.Text;
            box.CaretBrush = _palette.Text;
            box.SelectionBrush = _palette.Hover;
        }
        foreach (var line in SliceLayer.Children.OfType<Line>())
            line.Stroke = _palette.Separator;
        for (int i = 0; i < _slices.Count; i++)
            Paint(_slices[i], i == _hover);
    }

    static Point OnCircle(double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(OuterR + radius * Math.Sin(rad), OuterR - radius * Math.Cos(rad));
    }

    /// <summary>Ring sector between two radii, angles in degrees clockwise from the top.</summary>
    static Geometry Sector(double innerRadius, double outerRadius, double fromDegrees, double toDegrees)
    {
        var center = new Point(OuterR, OuterR);
        if (toDegrees - fromDegrees >= 358)
        {
            return new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(center, outerRadius, outerRadius),
                new EllipseGeometry(center, innerRadius, innerRadius));
        }
        bool large = toDegrees - fromDegrees > 180;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(OnCircle(outerRadius, fromDegrees), isFilled: true, isClosed: true);
            ctx.ArcTo(OnCircle(outerRadius, toDegrees), new Size(outerRadius, outerRadius), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(OnCircle(innerRadius, toDegrees), true, true);
            ctx.ArcTo(OnCircle(innerRadius, fromDegrees), new Size(innerRadius, innerRadius), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }
}
