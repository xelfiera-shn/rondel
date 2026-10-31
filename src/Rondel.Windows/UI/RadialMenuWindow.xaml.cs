using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Menus;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;
using Rondel.Windows.Interop;
using static Rondel.Windows.Interop.Native;
using Path = System.Windows.Shapes.Path;

namespace Rondel.Windows.UI;

/// <summary>
/// Borderless, per-pixel transparent, topmost radial menu. Created once at startup and only
/// moved/shown afterwards so it appears without delay. All geometry is in DIPs around the stage
/// center; screen positions are physical pixels of the monitor under the cursor.
/// <para>
/// The ring is made of separate tiles. The hovered tile lights up its rim in the brand gradient.
/// A slice that expands (Resume, into recent sessions) carries an arrow outside the ring; when it
/// is hovered, its items open on an outer arc centered on it.
/// </para>
/// </summary>
public partial class RadialMenuWindow : Window
{
    const double StageR = 300;         // the window is 2 × this; the ring center is its center
    const double OuterR = 168;         // ring radius (RingBase in the XAML is 2 × this)
    const double HubR = 52;            // hub disc radius (Hub in the XAML is 2 × this)
    const double TileInnerR = HubR + 7;
    const double TileOuterR = OuterR - 4;
    const double RimWidth = 4;         // lit strip along a tile's outer edge
    const double TileGap = 3;          // DIPs between neighbouring tiles (parallel edges)
    const double DeadZoneR = 58;       // closer than this to the center selects nothing
    const double LabelR = 113;         // where icon + label blocks are centered
    const double ArrowInnerR = OuterR + 5;
    const double ArrowOuterR = OuterR + 8;
    const double ArrowTipR = OuterR + 15;
    const double ChildInnerR = OuterR + 26;
    const double ChildOuterR = OuterR + 108;
    const double ChildLabelR = (ChildInnerR + ChildOuterR) / 2;
    const double ChildSpan = 34;       // degrees per expanded item
    const double MinLabelWidth = 56;
    const double ShadowPad = 32;       // shadow extent kept on-screen when clamping to the work area
    const int MaxPromptLength = 8000;  // keeps the encoded command line well under CreateProcess's limit

    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Label sizes to try, largest first: slices keep one size, expanded items may shrink a little.</summary>
    static readonly double[] SliceFontSizes = [12];
    static readonly double[] ChildFontSizes = [11.5, 11, 10.5];

    /// <summary>One drawn tile: a top-level slice or one of an expanded slice's items.</summary>
    sealed class Tile
    {
        public required MenuItemModel Item { get; init; }
        public required Path Fill { get; init; }
        public required Path Rim { get; init; }
        public required FrameworkElement Content { get; init; }
        public required TextBlock? Glyph { get; init; }
        public required TextBlock Label { get; init; }
        public required TextBlock Hint { get; init; }
        public required Brush Wash { get; init; }
        public required Brush Lit { get; init; }
        public required double Mid { get; init; }
        public bool Dimmed { get; set; }
        public Path? Arrow { get; set; }
        public IReadOnlyList<MenuItemModel> Children { get; set; } = [];
    }

    readonly List<Tile> _slices = [];
    readonly List<Tile> _children = [];
    nint _hwnd;
    MenuModel? _model;
    RondelPalette _palette = RondelPalette.For(SystemTheme.Read());
    Effect _glow;
    double _span = 360, _offset;
    int _hover = -1, _childHover = -1, _expanded = -1;
    double _childFrom, _childSpan;
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
        _glow = _palette.Glow;
        ApplyPalette();
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => { if (_isOpen && _input is null) SetHover(-1, -1); };
        MouseLeftButtonDown += (_, _) => _pressedInside = true;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => CloseMenu(restoreFocus: false);
        DpiChanged += (_, _) => { if (_isOpen) ApplyBounds(); };
        NameBox.TextChanged += (_, _) => { if (_input is not null && !_promptStage) UpdateNameFeedback(); };
    }

    /// <summary>A slice or an expanded item was chosen (click, release over it, number key, Enter).</summary>
    internal event Action<MenuModel, MenuItemModel>? ItemInvoked;

    internal event Action? MenuClosed;

    internal nint Handle => _hwnd;

    internal bool IsMenuOpen => _isOpen;

    /// <summary>Creates the window handle and renders once off-screen so the first real open is instant.</summary>
    internal void Prepare()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, GetWindowLongPtr(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        var agent = BuiltInAgents.ClaudeCode();
        var warmup = AppSettings.DefaultActions().Take(4)
            .Select(a => new MenuItemModel(a, agent, a.Id, a.Glyph, "warm-up", true)).ToList();
        var model = new MenuModel(TriggerContext.None("warm-up"), "warm-up", "warm-up", warmup);
        BuildSlices(model);
        _model = model;
        ApplyChildren(model, [.. warmup.Select((item, i) => i == 0 ? (IReadOnlyList<MenuItemModel>)warmup : [])], force: true);
        Expand(0);
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
        SetCenterTitle(model.Title);
        CenterSubtitle.Text = model.Subtitle ?? "";
        CenterSubtitle.Visibility = string.IsNullOrEmpty(model.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        CenterGlyph.Text = model.Context.Kind == ContextKind.DesktopEmpty ? "" : ""; // desktop / folder

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
        var (top, child) = HitTest((point.X - _centerX) / _scale, (point.Y - _centerY) / _scale);
        if (top >= 0)
            Invoke(top, child);
    }

    internal void CloseMenu(bool restoreFocus)
    {
        if (!_isOpen)
            return;
        _isOpen = false;
        int version = ++_version;
        SetHover(-1, -1);
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
            Collapse();
        };
        Surface.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.94, duration);
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    internal void UpdateTheme(ThemeColors theme)
    {
        _palette = RondelPalette.For(theme);
        _glow = _palette.Glow;
        ApplyPalette();
        if (!_isOpen)
            return;
        // Rebuild with the new colors, keeping what arrived after the menu opened.
        var hints = _slices.Select(s => (s.Hint.Visibility == Visibility.Visible ? s.Hint.Text : null, s.Dimmed)).ToList();
        var children = _slices.Select(s => s.Children).ToList();
        int expanded = _expanded;
        BuildSlices(_model!);
        ApplyHints(_model!, hints);
        ApplyChildren(_model!, children);
        if (expanded >= 0)
            Expand(expanded);
    }

    /// <summary>
    /// Developer aid (--render-menu): draws the menu in a given state into a bitmap, without showing
    /// the window, on a solid background so the shadows read as they would on screen.
    /// </summary>
    internal BitmapSource RenderPreview(MenuModel model, ThemeColors theme, IReadOnlyList<(string? Hint, bool Dimmed)> hints,
        IReadOnlyList<IReadOnlyList<MenuItemModel>> children, int hover, int childHover, Color background)
    {
        _palette = RondelPalette.For(theme);
        _glow = _palette.Glow;
        ApplyPalette();
        _model = model;
        BuildSlices(model);
        SetCenterTitle(model.Title);
        CenterSubtitle.Text = model.Subtitle ?? "";
        CenterGlyph.Text = model.Context.Kind == ContextKind.DesktopEmpty ? "" : "";
        ApplyHints(model, hints);
        ApplyChildren(model, children, force: true);
        if (hover >= 0)
        {
            SetHover(hover, -1);
            if (childHover >= 0 && _children.Count > 0)
                SetHover(hover, CenteredPosition(childHover, _children.Count));
        }

        // Animations do not run in a window that is not shown: jump to their end state.
        foreach (var element in new UIElement[] { Surface, ChildLayer }.Concat(_slices.Select(s => s.Arrow).OfType<UIElement>()))
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
        }
        foreach (var scale in new[] { SurfaceScale, ChildScale })
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        var size = new Size(Width, Height);
        Surface.Measure(size);
        Surface.Arrange(new Rect(size));
        Surface.UpdateLayout();
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(size));
        var bitmap = new RenderTargetBitmap((int)Width * 2, (int)Height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(backdrop);
        bitmap.Render(Surface);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Folder names are often one long word (MyCompanyProject): shrink such a name onto one line rather
    /// than breaking it in the middle. Names with spaces wrap onto two lines as usual.
    /// </summary>
    void SetCenterTitle(string title)
    {
        // Allow line breaks inside long names: "MyCompany|Project", "api_|gateway", "v2.|1".
        CenterTitle.Text = BreakableName(title);
        foreach (double size in new[] { 12.5, 12, 11.5, 11, 10.5 })
        {
            CenterTitle.FontSize = size;
            if (LongestWordWidth(CenterTitle) > CenterContent.Width)
                continue;
            var typeface = new Typeface(CenterTitle.FontFamily, CenterTitle.FontStyle, CenterTitle.FontWeight, CenterTitle.FontStretch);
            var text = new FormattedText(CenterTitle.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, size, Brushes.Black, 1.0)
            { MaxTextWidth = CenterContent.Width };
            if (text.Height <= CenterTitle.MaxHeight)
                return;
        }
        CenterTitle.FontSize = 12.5; // too long anyway: wrap and trim
    }

    /// <summary>Inserts zero-width spaces where a long name may wrap without breaking a word.</summary>
    static string BreakableName(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0)
            {
                char previous = name[i - 1];
                bool camel = char.IsLower(previous) && char.IsUpper(c);
                bool digits = char.IsLetter(previous) && char.IsDigit(c);
                bool separator = previous is '-' or '_' or '.';
                if (camel || digits || separator)
                    builder.Append('\u200B');
            }
            builder.Append(c);
        }
        return builder.ToString();
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
        var duration = AnimationTime(160);
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
        var (top, child) = HitTest(p.X - StageR, p.Y - StageR);
        SetHover(top, child);
    }

    void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool pressed = _pressedInside;
        _pressedInside = false;
        if (!_isOpen || !pressed || _input is not null)
            return;
        var p = e.GetPosition(MenuRoot);
        var (top, child) = HitTest(p.X - StageR, p.Y - StageR);
        if (top >= 0)
            Invoke(top, child);
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
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        int number = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => -1,
        };
        e.Handled = true;
        if (number >= 0)
        {
            if (_childHover >= 0 && number < _children.Count)
                Invoke(_expanded, number);
            else if (_childHover < 0 && number < n)
                Invoke(number, -1);
            return;
        }

        // Inside an expanded slice's items, the arrows and Tab move between the items.
        if (_childHover >= 0)
        {
            int k = _children.Count;
            switch (key)
            {
                case Key.Escape or Key.Back:
                    SetHover(_expanded, -1);
                    return;
                case Key.Enter or Key.Space:
                    Invoke(_expanded, _childHover);
                    return;
                case Key.Right or Key.Down or Key.Tab when !shift:
                    SetHover(_expanded, (_childHover + 1) % k);
                    return;
                case Key.Left or Key.Up or Key.Tab:
                    SetHover(_expanded, (_childHover - 1 + k) % k);
                    return;
            }
            e.Handled = false;
            return;
        }

        switch (key)
        {
            case Key.Escape:
                CloseMenu(restoreFocus: true);
                break;
            case Key.Enter or Key.Space:
                if (_hover >= 0)
                    Invoke(_hover, -1);
                break;
            case Key.Tab when _hover >= 0 && _slices[_hover].Children.Count > 0:
                // Tab on an expandable slice steps into its items, starting with the newest one in the middle.
                SetHover(_hover, CenteredPosition(0, _slices[_hover].Children.Count));
                break;
            case Key.Right or Key.Down or Key.Tab when !shift:
                SetHover(_hover < 0 ? 0 : (_hover + 1) % n, -1);
                break;
            case Key.Left or Key.Up or Key.Tab:
                SetHover(_hover < 0 ? n - 1 : (_hover - 1 + n) % n, -1);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    void Invoke(int top, int child)
    {
        if (_model is null || top < 0 || top >= _slices.Count)
            return;
        SetHover(top, child);
        var item = child >= 0 && child < _children.Count && top == _expanded ? _children[child].Item : _slices[top].Item;
        ItemInvoked?.Invoke(_model, item);
    }

    /// <summary>The slice and expanded item for an offset from the ring center (DIPs); -1 for none.</summary>
    (int Top, int Child) HitTest(double dx, double dy)
    {
        int n = _slices.Count;
        if (n == 0)
            return (-1, -1);
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Atan2(dx, -dy) * 180 / Math.PI; // 0° = up, clockwise

        if (_expanded >= 0 && _children.Count > 0 && distance > OuterR - 2 && distance <= ChildOuterR + 2)
        {
            double total = _children.Count * _childSpan;
            double fromStart = AngleBetween(_childFrom + total / 2, angle) + total / 2;
            if (fromStart >= -1 && fromStart <= total + 1)
            {
                // Between the ring and the items is a bridge that keeps the parent selected.
                return distance < ChildInnerR - 6
                    ? (_expanded, -1)
                    : (_expanded, Math.Clamp((int)(fromStart / _childSpan), 0, _children.Count - 1));
            }
        }

        if (distance < DeadZoneR || distance > ArrowTipR + 2)
            return (-1, -1);
        double relative = ((angle - _offset + _span / 2) % 360 + 360) % 360;
        int top = Math.Min((int)(relative / _span), n - 1);
        // Outside the ring only an expandable slice's arrow counts.
        if (distance > OuterR + 1 && _slices[top].Arrow is null)
            return (-1, -1);
        return (top, -1);
    }

    /// <summary>Signed angle from <paramref name="from"/> to <paramref name="to"/>, in (-180, 180].</summary>
    static double AngleBetween(double from, double to) => ((to - from) % 360 + 540) % 360 - 180;

    /// <summary>
    /// Late session hints (computed off the UI thread after the menu opened). Ignored if the menu
    /// has moved on to another context meanwhile.
    /// </summary>
    internal void ApplyHints(MenuModel model, IReadOnlyList<(string? Hint, bool Dimmed)> hints)
    {
        if (!ReferenceEquals(model, _model) || hints.Count != _slices.Count)
            return;
        for (int i = 0; i < hints.Count; i++)
        {
            var slice = _slices[i];
            if (hints[i].Hint is not { } text)
                continue;
            slice.Hint.Text = text;
            slice.Hint.Visibility = Visibility.Visible;
            slice.Dimmed = hints[i].Dimmed;
            Paint(slice, i == _hover, i == _expanded);
        }
    }

    /// <summary>
    /// Late items for expandable slices (e.g. recent sessions under Resume). A slice with items gets
    /// its arrow; if it is already hovered, it opens right away.
    /// </summary>
    internal void ApplyChildren(MenuModel model, IReadOnlyList<IReadOnlyList<MenuItemModel>> children, bool force = false)
    {
        if ((!_isOpen && !force) || !ReferenceEquals(model, _model) || children.Count != _slices.Count)
            return;
        for (int i = 0; i < children.Count; i++)
        {
            var slice = _slices[i];
            slice.Children = children[i];
            if (slice.Children.Count == 0 || slice.Arrow is not null)
                continue;
            double halfWidth = Math.Min(_span / 2 - 7, 15);
            slice.Arrow = new Path
            {
                Data = ArrowGeometry(slice.Mid, halfWidth),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
            };
            SliceLayer.Children.Add(slice.Arrow);
            slice.Arrow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, AnimationTime(160)));
            Paint(slice, i == _hover, i == _expanded);
        }
        if (_hover >= 0 && _hover != _expanded && _slices[_hover].Children.Count > 0)
            Expand(_hover);
    }

    // ---- expansion ----

    void Expand(int index)
    {
        if (index == _expanded)
            return;
        Collapse();
        var slice = _slices[index];
        int k = slice.Children.Count;
        if (k == 0)
            return;
        _expanded = index;
        _childSpan = Math.Min(ChildSpan, 300.0 / k);
        _childFrom = slice.Mid - k * _childSpan / 2;

        // Invisible but mouse-catching band from the ring to the items, so moving outward never falls through,
        // and a base band under the items, so the gaps between them look like the ring's.
        ChildLayer.Children.Add(new Path
        {
            Data = TileGeometry(OuterR - 2, ChildOuterR, _childFrom - 1, _childFrom + k * _childSpan + 1, 0),
            Fill = RondelPalette.Catcher,
        });
        ChildLayer.Children.Add(new Path
        {
            Data = TileGeometry(ChildInnerR - 4, ChildOuterR + 4, _childFrom - 1.5, _childFrom + k * _childSpan + 1.5, 0),
            Fill = _palette.RingFill,
            Stroke = _palette.RingStroke,
            StrokeThickness = 1,
        });

        // Items arrive newest first; the newest goes right outside the slice and the next ones alternate
        // clockwise and counterclockwise around it. _children is kept in position order (clockwise).
        double maxWidth = Math.Max(MinLabelWidth, 2 * ChildLabelR * Math.Sin(_childSpan * Math.PI / 360) - 10);
        var byPosition = new MenuItemModel[k];
        for (int rank = 0; rank < k; rank++)
            byPosition[CenteredPosition(rank, k)] = slice.Children[rank];
        for (int position = 0; position < k; position++)
        {
            double mid = _childFrom + (position + 0.5) * _childSpan;
            var tile = CreateTile(byPosition[position], mid, _childSpan, ChildInnerR, ChildOuterR, ChildLayer, withGlyph: false);
            tile.Label.MaxHeight = 46; // up to three lines of a session title
            PlaceLabel(tile.Content, tile.Label, mid, maxWidth, ChildLabelR, ChildInnerR, ChildOuterR, _childSpan, ChildFontSizes);
            _children.Add(tile);
            Paint(tile, hot: false, expanded: false);
        }
        Paint(slice, index == _hover, expanded: true);

        var duration = AnimationTime(150);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ChildLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        var grow = new DoubleAnimation(SystemParameters.ClientAreaAnimation ? 0.92 : 1, 1, duration) { EasingFunction = ease };
        ChildScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ChildScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>
    /// Clockwise position of the item with the given rank (0 = newest): 0 goes in the middle, 1 right
    /// after it, 2 right before it, and so on.
    /// </summary>
    static int CenteredPosition(int rank, int count)
    {
        int middle = (count - 1) / 2;
        int step = (rank + 1) / 2;
        return rank % 2 == 1 ? middle + step : middle - step;
    }

    void Collapse()
    {
        if (_expanded < 0)
            return;
        int index = _expanded;
        _expanded = -1;
        _childHover = -1;
        _children.Clear();
        ChildLayer.Children.Clear();
        if (index < _slices.Count)
            Paint(_slices[index], index == _hover, expanded: false);
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
        SetHover(-1, -1);
        Collapse();
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
        Collapse();
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
        string hintSample = Loc.Format("Time.Minutes", 59);

        for (int i = 0; i < n; i++)
        {
            var item = model.Items[i];
            double mid = _offset + i * _span;
            var tile = CreateTile(item, mid, _span, TileInnerR, TileOuterR, SliceLayer, withGlyph: true);
            bool expectsHint = item.Action.Hint != SessionHint.None;
            tile.Hint.Text = item.Hint ?? (expectsHint ? hintSample : "");
            tile.Hint.Visibility = item.Hint is not null ? Visibility.Visible
                : expectsHint ? Visibility.Hidden
                : Visibility.Collapsed;
            tile.Dimmed = item.Dimmed;
            PlaceLabel(tile.Content, tile.Label, mid, maxLabelWidth, LabelR, TileInnerR, TileOuterR, _span, SliceFontSizes);
            _slices.Add(tile);
            Paint(tile, hot: false, expanded: false);
        }
    }

    Tile CreateTile(MenuItemModel item, double mid, double span, double innerR, double outerR, Canvas layer, bool withGlyph)
    {
        double from = mid - span / 2, to = mid + span / 2;
        var fill = new Path { Data = TileGeometry(innerR, outerR, from, to, span < 360 ? TileGap : 0), StrokeThickness = 1 };
        var rim = new Path { Data = TileGeometry(outerR - RimWidth, outerR, from, to, span < 360 ? TileGap : 0) };
        layer.Children.Add(fill);
        layer.Children.Add(rim);

        TextBlock? glyph = withGlyph
            ? new TextBlock
            {
                FontFamily = IconFont,
                FontSize = 20,
                Text = item.Glyph.ToString(),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4),
            }
            : null;
        var label = new TextBlock
        {
            FontSize = 12,
            Text = item.Label,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
        };
        var hint = new TextBlock
        {
            FontSize = 10,
            Text = item.Hint ?? "",
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = item.Hint is null ? Visibility.Collapsed : Visibility.Visible,
        };
        var stack = new StackPanel();
        if (glyph is not null)
            stack.Children.Add(glyph);
        stack.Children.Add(label);
        stack.Children.Add(hint);
        layer.Children.Add(stack);

        // The rim's gradient runs along the arc; the wash deepens towards the outer edge.
        double half = Math.Min(span, 359) / 2;
        return new Tile
        {
            Item = item,
            Fill = fill,
            Rim = rim,
            Content = stack,
            Glyph = glyph,
            Label = label,
            Hint = hint,
            Mid = mid,
            Wash = _palette.WashBetween(OnCircle(innerR, mid), OnCircle(outerR, mid)),
            Lit = _palette.BrandBetween(OnCircle(outerR, mid - half), OnCircle(outerR, mid + half)),
        };
    }

    /// <summary>
    /// Centers a block on its middle angle, using the widest width (≤ max) at which the measured block
    /// stays inside its band and its own wedge. Narrower widths wrap the label onto a second line,
    /// which matters for the left/right slices where the ring is only ~100 DIP deep.
    /// </summary>
    static void PlaceLabel(FrameworkElement block, TextBlock label, double mid, double maxWidth,
        double labelR, double innerR, double outerR, double span, double[] fontSizes)
    {
        Size size = default;
        foreach (double fontSize in fontSizes)
        {
            label.FontSize = fontSize;
            // Never narrower than the longest word, so words are not broken in the middle.
            double minWidth = Math.Clamp(LongestWordWidth(label) + 2, MinLabelWidth, maxWidth);
            for (double width = maxWidth; width >= minWidth; width -= 2)
            {
                block.Width = width;
                block.Measure(new Size(width, double.PositiveInfinity));
                size = new Size(width, block.DesiredSize.Height);
                if (Fits(mid, size, labelR, innerR, outerR, span))
                {
                    Position(block, size, OnCircle(labelR, mid));
                    return;
                }
            }
        }
        Position(block, size, OnCircle(labelR, mid)); // best effort with the smallest size tried
    }

    static void Position(FrameworkElement block, Size size, Point center)
    {
        Canvas.SetLeft(block, center.X - size.Width / 2);
        Canvas.SetTop(block, center.Y - size.Height / 2);
    }

    static double LongestWordWidth(TextBlock text)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        return text.Text.Split([' ', '\u200B'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new FormattedText(word, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, text.FontSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace)
            .DefaultIfEmpty(0)
            .Max();
    }

    static bool Fits(double mid, Size size, double labelR, double innerR, double outerR, double span)
    {
        var center = OnCircle(labelR, mid);
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            double dx = center.X + sx * size.Width / 2 - StageR;
            double dy = center.Y + sy * size.Height / 2 - StageR;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            if (radius < innerR + 2 || radius > outerR - RimWidth - 1)
                return false;
            if (span < 360)
            {
                double angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
                if (Math.Abs(AngleBetween(mid, angle)) > span / 2 - 3)
                    return false;
            }
        }
        return true;
    }

    void SetHover(int top, int child)
    {
        if (top == _hover && child == _childHover)
            return;
        if (top != _hover)
        {
            int previous = _hover;
            _hover = top;
            if (previous >= 0 && previous < _slices.Count)
                Paint(_slices[previous], hot: false, previous == _expanded);
            if (_hover >= 0 && _hover < _slices.Count)
                Paint(_slices[_hover], hot: true, _hover == _expanded);
        }
        // Hovering another slice closes an open one and opens the new one if it expands.
        // Leaving the ring (center, outside) keeps it open, so the pointer can travel.
        if (top >= 0 && top != _expanded)
        {
            if (_slices[top].Children.Count > 0)
                Expand(top);
            else
                Collapse();
        }
        // After a possible expansion, so the item refers to the slice's current items.
        if (child != _childHover)
        {
            if (_childHover >= 0 && _childHover < _children.Count)
                Paint(_children[_childHover], hot: false, expanded: false);
            _childHover = child < _children.Count ? child : -1;
            if (_childHover >= 0)
                Paint(_children[_childHover], hot: true, expanded: false);
        }
    }

    void Paint(Tile tile, bool hot, bool expanded)
    {
        bool lit = hot || expanded;
        tile.Fill.Fill = hot ? tile.Wash : _palette.TileFill;
        tile.Fill.Stroke = _palette.TileStroke;
        tile.Rim.Fill = lit ? tile.Lit : _palette.RimIdle;
        tile.Rim.Effect = lit ? _glow : null;
        if (tile.Glyph is not null)
            tile.Glyph.Foreground = hot ? _palette.Accent : _palette.Text;
        tile.Label.Foreground = _palette.Text;
        tile.Hint.Foreground = hot ? _palette.Accent : _palette.SubText;
        tile.Content.Opacity = tile.Dimmed && !hot ? 0.45 : 1;
        if (tile.Arrow is not null)
        {
            tile.Arrow.Fill = tile.Lit;
            tile.Arrow.Stroke = tile.Lit;
            tile.Arrow.Opacity = lit ? 1 : 0.5;
            tile.Arrow.Effect = lit ? _glow : null;
        }
    }

    void ApplyPalette()
    {
        RingBase.Fill = _palette.RingFill;
        RingBase.Stroke = _palette.RingStroke;
        RingShadow.Opacity = _palette.ShadowOpacity;
        ChildShadow.Opacity = _palette.ShadowOpacity * 0.8;
        Hub.Fill = _palette.HubFill;
        HubRing.Stroke = _palette.Brand;
        CenterGlyph.Foreground = _palette.Brand;
        CenterTitle.Foreground = _palette.Text;
        CenterSubtitle.Foreground = _palette.SubText;
        InputCard.Background = _palette.Card;
        InputCard.BorderBrush = _palette.Brand;
        CardShadow.Opacity = _palette.ShadowOpacity;
        InputGlyph.Foreground = _palette.Brand;
        InputTitle.Foreground = _palette.Text;
        InputLocation.Foreground = _palette.SubText;
        foreach (var box in new[] { NameBox, PromptBox })
        {
            box.Background = _palette.InputBackground;
            box.BorderBrush = _palette.Accent;
            box.Foreground = _palette.Text;
            box.CaretBrush = _palette.Text;
            box.SelectionBrush = _palette.Accent;
        }
    }

    static Point OnCircle(double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(StageR + radius * Math.Sin(rad), StageR - radius * Math.Cos(rad));
    }

    /// <summary>
    /// Ring sector between two radii, angles in degrees clockwise from the top. Its side edges are
    /// pulled in by half of <paramref name="gap"/>, so neighbouring tiles have parallel gaps.
    /// </summary>
    static Geometry TileGeometry(double innerR, double outerR, double fromDegrees, double toDegrees, double gap)
    {
        var center = new Point(StageR, StageR);
        if (toDegrees - fromDegrees >= 358)
        {
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(center, outerR, outerR),
                new EllipseGeometry(center, innerR, innerR));
            ring.Freeze();
            return ring;
        }
        double outerInset = Math.Asin(gap / 2 / outerR) * 180 / Math.PI;
        double innerInset = Math.Asin(gap / 2 / innerR) * 180 / Math.PI;
        double outerFrom = fromDegrees + outerInset, outerTo = toDegrees - outerInset;
        double innerFrom = fromDegrees + innerInset, innerTo = toDegrees - innerInset;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(OnCircle(outerR, outerFrom), isFilled: true, isClosed: true);
            ctx.ArcTo(OnCircle(outerR, outerTo), new Size(outerR, outerR), 0, outerTo - outerFrom > 180, SweepDirection.Clockwise, true, true);
            ctx.LineTo(OnCircle(innerR, innerTo), true, true);
            ctx.ArcTo(OnCircle(innerR, innerFrom), new Size(innerR, innerR), 0, innerTo - innerFrom > 180, SweepDirection.Counterclockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>A short band outside the ring whose outer edge comes to a point: "this slice opens outward".</summary>
    static Geometry ArrowGeometry(double mid, double halfWidth)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(OnCircle(ArrowInnerR, mid - halfWidth), isFilled: true, isClosed: true);
            ctx.ArcTo(OnCircle(ArrowInnerR, mid + halfWidth), new Size(ArrowInnerR, ArrowInnerR), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(OnCircle(ArrowOuterR, mid + halfWidth), true, true);
            ctx.LineTo(OnCircle(ArrowTipR, mid), true, true);
            ctx.LineTo(OnCircle(ArrowOuterR, mid - halfWidth), true, true);
        }
        geometry.Freeze();
        return geometry;
    }
}
