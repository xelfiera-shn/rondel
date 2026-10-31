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
using Rondel.Core.Flow;
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
/// A tile that opens further (Resume into sessions, Recent Projects into folders, a folder into its
/// sessions) carries an arrow outside its band; when it is hovered, its items open on the next band
/// out, an arc centered on it. The ring is band 0, every arc one band further out.
/// </para>
/// </summary>
public partial class RadialMenuWindow : Window
{
    const double StageR = 410;         // the window is 2 × this; the ring center is its center
    const double OuterR = 168;         // ring radius (RingBase in the XAML is 2 × this)
    const double HubR = 52;            // hub disc radius (Hub in the XAML is 2 × this)
    const double TileInnerR = HubR + 7;
    const double TileOuterR = OuterR - 4;
    const double RimWidth = 4;         // lit strip along a tile's outer edge
    const double TileGap = 3;          // DIPs between neighbouring tiles (parallel edges)
    const double DeadZoneR = 58;       // closer than this to the center selects nothing
    const double LabelR = 113;         // where icon + label blocks are centered
    const double ArcGap = 26;          // from a band's edge to the tiles of the arc it opens (room for the arrow)
    const double ArrowReach = 17;      // how far beyond its band's edge an arrow (and its hit area) reaches
    const double MinLabelWidth = 56;
    const double UnbrokenSlack = 4;    // how much closer to the edges an unbroken folder name may come
    const double ShadowPad = 32;       // shadow extent kept on-screen when clamping to the work area
    const int MaxPromptLength = 8000;  // keeps the encoded command line well under CreateProcess's limit

    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>One drawn tile: a slice of the ring or an item on an arc.</summary>
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

    /// <summary>
    /// One band of tiles. Band 0 is the ring; band n + 1 is the arc opened from a tile of band n,
    /// centered on that tile (or turned towards the free side near a screen edge).
    /// </summary>
    sealed class Band
    {
        public required Canvas Layer { get; init; }
        public ScaleTransform? Scale { get; init; }
        public required double InnerR { get; init; }
        public required double OuterR { get; init; }

        /// <summary>Visual outer edge (the base under the tiles); arrows start just outside it.</summary>
        public required double EdgeR { get; init; }

        /// <summary>Degrees per tile on an arc (the ring divides the circle among its slices instead).</summary>
        public required double TileSpan { get; init; }

        public required double LabelMaxHeight { get; init; }

        /// <summary>Label sizes to try, largest first.</summary>
        public required double[] FontSizes { get; init; }

        public List<Tile> Tiles { get; } = [];

        /// <summary>The tile whose items fill the next band, or -1.</summary>
        public int Opened { get; set; } = -1;

        /// <summary>Arcs: start angle and degrees per tile; the bridge back to the parent band.</summary>
        public double From { get; set; }

        public double Span { get; set; }

        public double BridgeFrom { get; set; }

        public double BridgeTo { get; set; }

        /// <summary>Clockwise position of the tile with each rank (0 = newest).</summary>
        public int[] PositionOfRank { get; set; } = [];
    }

    readonly Band[] _bands;
    nint _hwnd;
    MenuModel? _model;
    RondelPalette _palette = RondelPalette.For(SystemTheme.Read());
    Effect _glow;
    double _span = 360, _offset;
    int _hoverLevel = -1, _hoverIndex = -1;
    /// <summary>Room around the ring center up to the monitor's work area (DIPs): left, top, right, bottom.</summary>
    (double Left, double Top, double Right, double Bottom) _room = (double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
    bool _isOpen;
    int _version;
    bool _pressedInside;
    RECT _bounds;
    double _centerX, _centerY, _scale = 1;
    RECT _work; // the work area of the monitor the menu is on (physical pixels)
    nint _previousForeground;
    InputSpec? _input;
    bool _promptStage;
    string _acceptedName = "";

    /// <summary>
    /// Label layouts already worked out while this menu is open: arcs open again and again as the pointer
    /// moves between tiles, and fitting a label takes many measurements.
    /// </summary>
    readonly Dictionary<(string Label, string Hint, double Mid, double Span, double Radius), (string Text, double FontSize, Size Size)> _labelLayouts = [];

    public RadialMenuWindow()
    {
        InitializeComponent();
        _bands =
        [
            new Band { Layer = SliceLayer, InnerR = TileInnerR, OuterR = TileOuterR, EdgeR = OuterR, TileSpan = 0, LabelMaxHeight = 34, FontSizes = [12] },
            // Session titles and folder names: up to three lines, slightly smaller if needed.
            new Band
            {
                Layer = ChildLayer, Scale = ChildScale, InnerR = OuterR + ArcGap, OuterR = OuterR + ArcGap + 82, EdgeR = OuterR + ArcGap + 86,
                TileSpan = 34, LabelMaxHeight = 46, FontSizes = [11.5, 11, 10.5],
            },
            // A recent project's sessions: a narrower band further out.
            new Band
            {
                Layer = GrandLayer, Scale = GrandScale, InnerR = OuterR + ArcGap + 86 + ArcGap, OuterR = OuterR + ArcGap + 86 + ArcGap + 76,
                EdgeR = OuterR + ArcGap + 86 + ArcGap + 80, TileSpan = 24, LabelMaxHeight = 46, FontSizes = [11, 10.5, 10],
            },
        ];
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
        NameBox.GotKeyboardFocus += (_, _) => { if (_newProject is not null) UpdateNewProjectHint(); };
        PromptBox.GotKeyboardFocus += (_, _) => { if (_newProject is not null) UpdateNewProjectHint(); };
        OpenTemplates.MouseLeftButtonUp += (_, _) => OnOpenTemplates();
        InputCard.SizeChanged += (_, _) => FitInputCard();
    }

    /// <summary>A slice or an item on an arc was chosen (click, release over it, number key, Enter).</summary>
    internal event Action<MenuModel, MenuItemModel>? ItemInvoked;

    internal event Action? MenuClosed;

    internal nint Handle => _hwnd;

    internal bool IsMenuOpen => _isOpen;

    List<Tile> Slices => _bands[0].Tiles;

    /// <summary>Creates the window handle and renders once off-screen so the first real open is instant.</summary>
    internal void Prepare()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, GetWindowLongPtr(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        // Warm up every band: the first slice opens into the warm-up items, the first of those into them again.
        var agent = BuiltInAgents.ClaudeCode();
        var warmup = AppSettings.DefaultActions().Take(4)
            .Select(a => new MenuItemModel(a, agent, a.Id, a.Glyph, "warm-up", true)).ToList();
        var nested = warmup.Select((item, i) => i == 0 ? item with { Children = warmup } : item).ToList();
        var model = new MenuModel(TriggerContext.None("warm-up"), "warm-up", "warm-up", warmup);
        _model = model;
        BuildSlices(model);
        ApplyChildren(model, [.. warmup.Select((item, i) => i == 0 ? (IReadOnlyList<MenuItemModel>)nested : [])], force: true);
        SetHover(0, 0);
        SetHover(1, _bands[1].PositionOfRank[0]);
        SetHover(-1, -1);
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
        _labelLayouts.Clear();
        Width = Height = StageSizeFor(model);
        BuildSlices(model);
        SetCenterTitle(model.Title);
        CenterSubtitle.Text = model.Subtitle ?? "";
        CenterSubtitle.Visibility = string.IsNullOrEmpty(model.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        CenterGlyph.Text = CenterGlyphFor(model.Context.Kind);

        // Center on the cursor, but keep the ring and its shadow inside the monitor's work area.
        var (work, dpi) = GetMonitorWorkArea(anchor);
        _work = work;
        _scale = dpi / 96.0;
        CacheRingBase(_scale);
        int size = (int)Math.Round(Width * _scale);
        double half = (OuterR + ShadowPad) * _scale;
        int cx = ClampCenter(anchor.X, work.Left + half, work.Right - half);
        int cy = ClampCenter(anchor.Y, work.Top + half, work.Bottom - half);
        _bounds = new RECT { Left = cx - size / 2, Top = cy - size / 2 };
        _bounds.Right = _bounds.Left + size;
        _bounds.Bottom = _bounds.Top + size;
        _centerX = _bounds.Left + size / 2.0;
        _centerY = _bounds.Top + size / 2.0;
        _room = ((_centerX - work.Left) / _scale, (_centerY - work.Top) / _scale,
            (work.Right - _centerX) / _scale, (work.Bottom - _centerY) / _scale);

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
        var (level, index) = HitTest((point.X - _centerX) / _scale, (point.Y - _centerY) / _scale);
        if (level >= 0)
            Invoke(level, index);
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
            CloseFrom(1);
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
        // Rebuild with the new colors, keeping what arrived after the menu opened and what is open.
        var hints = Slices.Select(s => (s.Hint.Visibility == Visibility.Visible ? s.Hint.Text : null, s.Dimmed)).ToList();
        var children = Slices.Select(s => s.Children).ToList();
        var opened = _bands.Select(b => b.Opened).ToList();
        BuildSlices(_model!);
        ApplyHints(_model!, hints);
        ApplyChildren(_model!, children);
        for (int level = 0; level < _bands.Length - 1 && opened[level] >= 0 && opened[level] < _bands[level].Tiles.Count; level++)
            Open(level, opened[level]);
    }

    /// <summary>
    /// Developer aid (--render-menu): draws the menu in a given state into a bitmap, without showing
    /// the window, on a solid background so the shadows read as they would on screen.
    /// </summary>
    /// <param name="path">What to hover: a slice index, then for each arc the rank of an item (0 = newest).</param>
    internal BitmapSource RenderPreview(MenuModel model, ThemeColors theme, IReadOnlyList<(string? Hint, bool Dimmed)> hints,
        IReadOnlyList<IReadOnlyList<MenuItemModel>> children, IReadOnlyList<int> path, Color background, bool atLeftEdge = false)
    {
        // As if the menu opened at the left edge of the work area (the ring clamped against it).
        _room = atLeftEdge
            ? (OuterR + ShadowPad, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity)
            : (double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        _palette = RondelPalette.For(theme);
        _glow = _palette.Glow;
        ApplyPalette();
        _model = model;
        Width = Height = StageSizeFor(model);
        CacheRingBase(2); // the preview is drawn at 192 DPI
        BuildSlices(model);
        SetCenterTitle(model.Title);
        CenterSubtitle.Text = model.Subtitle ?? "";
        CenterGlyph.Text = CenterGlyphFor(model.Context.Kind);
        ApplyHints(model, hints);
        ApplyChildren(model, children, force: true);
        if (path.Count > 0)
            SetHover(0, path[0]);
        for (int level = 1; level < path.Count && _bands[level].Tiles.Count > 0; level++)
            SetHover(level, _bands[level].PositionOfRank[Math.Min(path[level], _bands[level].Tiles.Count - 1)]);

        // Animations do not run in a window that is not shown: jump to their end state.
        var animated = new UIElement[] { Surface }
            .Concat(_bands.Select(b => b.Layer))
            .Concat(_bands.SelectMany(b => b.Tiles).Select(t => t.Arrow).OfType<UIElement>());
        foreach (var element in animated)
            element.BeginAnimation(OpacityProperty, null);
        foreach (var scale in new[] { SurfaceScale }.Concat(_bands.Select(b => b.Scale).OfType<ScaleTransform>()))
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        Surface.Opacity = 1;
        foreach (var band in _bands)
            band.Layer.Opacity = 1;

        return RenderSurface(background);
    }

    /// <summary>Lays out the window's content at its size and draws it over <paramref name="background"/> at 192 DPI.</summary>
    BitmapSource RenderSurface(Color background)
    {
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

    static string CenterGlyphFor(ContextKind kind) => kind switch
    {
        ContextKind.DesktopEmpty => "\uE7F4", // TVMonitor
        ContextKind.FileItem => "\uE7C3",     // Page
        _ => "\uE8B7",                        // Folder
    };

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
                    builder.Append('​');
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    // ---- placement, focus, animation ----

    /// <summary>
    /// The window is only as big as the deepest arc this menu can open (none, sessions, or recent projects and
    /// their sessions): a per-pixel transparent window is copied whole for every frame, so size is speed.
    /// The stage keeps its full size and is centered in the window, so all geometry stays the same.
    /// </summary>
    double StageSizeFor(MenuModel model)
    {
        int depth = model.Items.Any(MenuBuilder.ListsRecentProjects) ? 2 : model.Items.Any(MenuBuilder.CanExpand) ? 1 : 0;
        double reach = (depth == 0 ? OuterR : _bands[depth].EdgeR) + ArrowReach + ShadowPad;
        return Math.Min(2 * StageR, Math.Ceiling(2 * reach));
    }

    /// <summary>
    /// The input card can be taller than the ring (the New Project card with its prompt): it moves from the menu's
    /// center just far enough to stay inside the work area (top first when it is taller than the screen), and the
    /// window grows around the same center to hold it, so the fading ring does not move.
    /// </summary>
    void FitInputCard()
    {
        if (!_isOpen || _input is null || InputCard.ActualHeight <= 0)
            return;
        double width = (InputCard.ActualWidth + 2 * ShadowPad) * _scale;
        double height = (InputCard.ActualHeight + 2 * ShadowPad) * _scale;
        double dx = ShiftInto(_centerX - width / 2, width, _work.Left, _work.Right);
        double dy = ShiftInto(_centerY - height / 2, height, _work.Top, _work.Bottom);
        InputShift.X = dx / _scale;
        InputShift.Y = dy / _scale;

        double halfWidth = Math.Max(_bounds.Width / 2.0, Math.Abs(dx) + width / 2);
        double halfHeight = Math.Max(_bounds.Height / 2.0, Math.Abs(dy) + height / 2);
        if (2 * halfWidth <= _bounds.Width + 1 && 2 * halfHeight <= _bounds.Height + 1)
            return;
        _bounds = new RECT
        {
            Left = (int)Math.Floor(_centerX - halfWidth),
            Top = (int)Math.Floor(_centerY - halfHeight),
            Right = (int)Math.Ceiling(_centerX + halfWidth),
            Bottom = (int)Math.Ceiling(_centerY + halfHeight),
        };
        Width = _bounds.Width / _scale;
        Height = _bounds.Height / _scale;
        ApplyBounds();
    }

    /// <summary>How far a span (start, length) has to move to lie within [min, max]; a longer one is aligned to min.</summary>
    static double ShiftInto(double start, double length, double min, double max) =>
        start < min || length > max - min ? min - start
        : start + length > max ? max - (start + length)
        : 0;

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
        var grow = new DoubleAnimation(Animate ? 0.86 : 1, 1, duration) { EasingFunction = ease };
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        SurfaceScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>
    /// The ring's base disc never changes while the menu is open, but its large blurred shadow was redrawn
    /// whenever anything above it changed. Cached as a bitmap at the monitor's scale, it stays sharp.
    /// (Layers with text are not cached: a cached bitmap would soften the text.)
    /// </summary>
    void CacheRingBase(double scale)
    {
        if (RingBase.CacheMode is BitmapCache { RenderAtScale: var current } && current == scale)
            return;
        RingBase.CacheMode = new BitmapCache(scale) { SnapsToDevicePixels = true };
    }

    /// <summary>
    /// Animations run unless "Animation effects" is off in Windows or the PC runs on battery: every frame of
    /// a large layered window costs noticeably there, and an instant menu feels faster than a slow animation.
    /// </summary>
    static bool Animate => SystemParameters.ClientAreaAnimation && SystemParameters.PowerLineStatus != PowerLineStatus.Offline;

    static Duration AnimationTime(int milliseconds) => TimeSpan.FromMilliseconds(Animate ? milliseconds : 0);

    static int ClampCenter(int value, double min, double max) =>
        min > max ? (int)Math.Round((min + max) / 2) : (int)Math.Round(Math.Clamp(value, min, max));

    // ---- input ----

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isOpen || _input is not null)
            return;
        var p = e.GetPosition(MenuRoot);
        var (level, index) = HitTest(p.X - StageR, p.Y - StageR);
        SetHover(level, index);
    }

    void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool pressed = _pressedInside;
        _pressedInside = false;
        if (!_isOpen || !pressed || _input is not null)
            return;
        var p = e.GetPosition(MenuRoot);
        var (level, index) = HitTest(p.X - StageR, p.Y - StageR);
        if (level >= 0)
            Invoke(level, index);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isOpen && _input is not null)
        {
            OnInputKeyDown(e);
            return;
        }
        if (!_isOpen || Slices.Count == 0)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        int number = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => -1,
        };
        // Keys act on the band the selection is in: the ring, or an open arc.
        int level = Math.Max(_hoverLevel, 0);
        var band = _bands[level];
        int count = band.Tiles.Count;
        e.Handled = true;
        if (number >= 0)
        {
            // Ring: slices clockwise from the top. Arcs: by recency, 1 = newest.
            if (number < count)
                Invoke(level, level == 0 ? number : band.PositionOfRank[number]);
            return;
        }
        switch (key)
        {
            case Key.Escape or Key.Back when _hoverLevel > 0:
                SetHover(_hoverLevel - 1, _bands[_hoverLevel - 1].Opened);
                break;
            case Key.Escape:
                CloseMenu(restoreFocus: true);
                break;
            case Key.Enter or Key.Space:
                if (_hoverLevel >= 0)
                    Invoke(_hoverLevel, _hoverIndex);
                break;
            case Key.Tab when !shift && StepIn():
                break;
            case Key.Right or Key.Down or Key.Tab when !shift:
                SetHover(level, _hoverLevel < 0 ? 0 : (_hoverIndex + 1) % count);
                break;
            case Key.Left or Key.Up or Key.Tab:
                SetHover(level, _hoverLevel < 0 ? count - 1 : (_hoverIndex - 1 + count) % count);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    /// <summary>Moves the selection into the items of the selected tile, starting with the newest.</summary>
    /// <returns>False when the selected tile has no items.</returns>
    bool StepIn()
    {
        if (_hoverLevel < 0 || _hoverLevel + 1 >= _bands.Length)
            return false;
        var band = _bands[_hoverLevel];
        if (band.Tiles[_hoverIndex].Children.Count == 0)
            return false;
        if (band.Opened != _hoverIndex)
            Open(_hoverLevel, _hoverIndex);
        SetHover(_hoverLevel + 1, _bands[_hoverLevel + 1].PositionOfRank[0]);
        return true;
    }

    void Invoke(int level, int index)
    {
        if (_model is null || level < 0 || index < 0 || index >= _bands[level].Tiles.Count)
            return;
        var tile = _bands[level].Tiles[index];
        SetHover(level, index);
        // The Recent Projects slice launches nothing itself: choosing it steps into its projects.
        if (tile.Item.Action.Source == ActionSource.RecentProjects && tile.Item.Directory is null)
        {
            StepIn();
            return;
        }
        ItemInvoked?.Invoke(_model, tile.Item);
    }

    /// <summary>The band and tile under an offset from the ring center (DIPs); (-1, -1) for none.</summary>
    (int Level, int Index) HitTest(double dx, double dy)
    {
        if (Slices.Count == 0)
            return (-1, -1);
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Atan2(dx, -dy) * 180 / Math.PI; // 0° = up, clockwise

        // Open arcs, outermost first. Each has its tiles (with the arrows of tiles that open further) and,
        // between it and the band it was opened from, a bridge that keeps the opening tile selected.
        for (int level = _bands.Length - 1; level >= 1; level--)
        {
            var band = _bands[level];
            if (band.Tiles.Count == 0)
                continue;
            if (distance >= band.InnerR - 6 && distance <= band.EdgeR + ArrowReach)
            {
                double total = band.Tiles.Count * band.Span;
                double fromStart = AngleBetween(band.From + total / 2, angle) + total / 2;
                if (fromStart >= -1 && fromStart <= total + 1)
                {
                    int index = Math.Clamp((int)(fromStart / band.Span), 0, band.Tiles.Count - 1);
                    if (distance <= band.EdgeR + 1 || band.Tiles[index].Arrow is not null)
                        return (level, index);
                }
            }
            else if (distance > _bands[level - 1].EdgeR - 2 && distance < band.InnerR - 6)
            {
                double width = band.BridgeTo - band.BridgeFrom;
                double fromStart = AngleBetween(band.BridgeFrom + width / 2, angle) + width / 2;
                if (fromStart >= -1 && fromStart <= width + 1)
                    return (level - 1, _bands[level - 1].Opened);
            }
        }

        if (distance < DeadZoneR || distance > OuterR + ArrowReach)
            return (-1, -1);
        double relative = ((angle - _offset + _span / 2) % 360 + 360) % 360;
        int top = Math.Min((int)(relative / _span), Slices.Count - 1);
        // Outside the ring only an expandable slice's arrow counts.
        if (distance > OuterR + 1 && Slices[top].Arrow is null)
            return (-1, -1);
        return (0, top);
    }

    /// <summary>Signed angle from <paramref name="from"/> to <paramref name="to"/>, in (-180, 180].</summary>
    static double AngleBetween(double from, double to) => ((to - from) % 360 + 540) % 360 - 180;

    /// <summary>
    /// Late session hints (computed off the UI thread after the menu opened). Ignored if the menu
    /// has moved on to another context meanwhile.
    /// </summary>
    internal void ApplyHints(MenuModel model, IReadOnlyList<(string? Hint, bool Dimmed)> hints)
    {
        if (!ReferenceEquals(model, _model) || hints.Count != Slices.Count)
            return;
        for (int i = 0; i < hints.Count; i++)
        {
            var slice = Slices[i];
            if (hints[i].Hint is not { } text)
                continue;
            slice.Hint.Text = text;
            slice.Hint.Visibility = Visibility.Visible;
            slice.Dimmed = hints[i].Dimmed;
            Paint(slice, IsHot(0, i), _bands[0].Opened == i);
        }
    }

    /// <summary>
    /// Late items for slices that open further (recent sessions, recent projects). A slice with items
    /// gets its arrow; if it is already hovered, it opens right away.
    /// </summary>
    internal void ApplyChildren(MenuModel model, IReadOnlyList<IReadOnlyList<MenuItemModel>> children, bool force = false)
    {
        if ((!_isOpen && !force) || !ReferenceEquals(model, _model) || children.Count != Slices.Count)
            return;
        for (int i = 0; i < children.Count; i++)
        {
            var slice = Slices[i];
            slice.Children = children[i];
            if (slice.Children.Count == 0 || slice.Arrow is not null)
                continue;
            AddArrow(0, slice);
            Paint(slice, IsHot(0, i), _bands[0].Opened == i);
        }
        if (_hoverLevel == 0 && _bands[0].Opened != _hoverIndex && Slices[_hoverIndex].Children.Count > 0)
            Open(0, _hoverIndex);
    }

    // ---- opening arcs ----

    /// <summary>Opens the items of tile <paramref name="index"/> of band <paramref name="level"/> on the next band out.</summary>
    void Open(int level, int index)
    {
        var parent = _bands[level];
        var tile = parent.Tiles[index];
        var band = _bands[level + 1];
        CloseFrom(level + 1);
        int k = tile.Children.Count;
        if (k == 0)
            return;
        parent.Opened = index;
        band.Span = Math.Min(band.TileSpan, 300.0 / k);
        double total = k * band.Span;
        band.From = tile.Mid + ArcShift(tile.Mid, total, band.EdgeR) - total / 2;
        // The bridge reaches from the tile's arrow to the items, even when the arc had to turn away from an edge.
        double arrowHalf = ArrowHalfWidth(level);
        double startOffset = AngleBetween(tile.Mid, band.From), endOffset = startOffset + total;
        band.BridgeFrom = tile.Mid + Math.Min(startOffset, -arrowHalf) - 1;
        band.BridgeTo = tile.Mid + Math.Max(endOffset, arrowHalf) + 1;

        // Invisible but mouse-catching band from the parent to the items, so moving outward never falls through,
        // and a base under the items, so the gaps between them look like the ring's.
        band.Layer.Children.Add(new Path
        {
            Data = TileGeometry(parent.EdgeR - 2, band.InnerR, band.BridgeFrom, band.BridgeTo, 0),
            Fill = RondelPalette.Catcher,
        });
        band.Layer.Children.Add(new Path
        {
            Data = TileGeometry(band.InnerR - 4, band.EdgeR, band.From - 1.5, band.From + total + 1.5, 0),
            Fill = _palette.RingFill,
            Stroke = _palette.RingStroke,
            StrokeThickness = 1,
        });

        // Items arrive newest first. The newest takes the place closest to the tile (right outside it when the
        // arc is centered) and the next ones the next closest, alternating clockwise and counterclockwise.
        // Near a screen edge, where the arc had to turn, the newest is at the end next to the tile.
        // Tiles are kept in position order (clockwise).
        double labelR = (band.InnerR + band.OuterR) / 2;
        double maxWidth = Math.Max(MinLabelWidth, 2 * labelR * Math.Sin(band.Span * Math.PI / 360) - 10);
        band.PositionOfRank = Enumerable.Range(0, k)
            .OrderBy(position => Math.Round(Math.Abs(AngleBetween(tile.Mid, band.From + (position + 0.5) * band.Span)), 2))
            .ThenByDescending(position => position)
            .ToArray();
        var byPosition = new MenuItemModel[k];
        for (int rank = 0; rank < k; rank++)
            byPosition[band.PositionOfRank[rank]] = tile.Children[rank];
        // Recent projects are folder names, often one long word (Contoso.Platform.Web, my_project); session titles
        // are sentences and only wrap between words.
        bool folderNames = MenuBuilder.ListsRecentProjects(tile.Item);
        for (int position = 0; position < k; position++)
        {
            double mid = band.From + (position + 0.5) * band.Span;
            var item = CreateTile(byPosition[position], mid, band.Span, band.InnerR, band.OuterR, band.Layer, withGlyph: false);
            item.Children = byPosition[position].Children;
            item.Label.MaxHeight = band.LabelMaxHeight;
            PlaceLabel(item, mid, maxWidth, labelR, band.InnerR, band.OuterR, band.Span, band.FontSizes, folderName: folderNames);
            band.Tiles.Add(item);
            if (item.Children.Count > 0 && level + 2 < _bands.Length)
                AddArrow(level + 1, item);
            Paint(item, hot: false, expanded: false);
        }
        Paint(tile, IsHot(level, index), expanded: true);

        if (band.Scale is { } scale)
        {
            var duration = AnimationTime(150);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            band.Layer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            var grow = new DoubleAnimation(Animate ? 0.94 : 1, 1, duration) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    /// <summary>Closes the arcs from band <paramref name="level"/> outward.</summary>
    void CloseFrom(int level)
    {
        for (int l = _bands.Length - 1; l >= Math.Max(level, 1); l--)
        {
            var band = _bands[l];
            if (band.Tiles.Count == 0 && band.Layer.Children.Count == 0)
                continue;
            band.Tiles.Clear();
            band.Layer.Children.Clear();
            band.Opened = -1;
            band.PositionOfRank = [];
            if (_hoverLevel == l)
                (_hoverLevel, _hoverIndex) = (-1, -1);
            var parent = _bands[l - 1];
            int opener = parent.Opened;
            parent.Opened = -1;
            if (opener >= 0 && opener < parent.Tiles.Count)
                Paint(parent.Tiles[opener], IsHot(l - 1, opener), expanded: false);
        }
    }

    /// <summary>
    /// How far (degrees) an arc of <paramref name="total"/> degrees reaching out to <paramref name="edgeR"/> has to
    /// turn away from being centered on <paramref name="mid"/> to stay inside the monitor's work area: 0 when it
    /// fits, else the smallest turn that fits (clockwise first). Near a screen edge the items open towards the free side.
    /// </summary>
    double ArcShift(double mid, double total, double edgeR)
    {
        const double Margin = 8;
        double reach = edgeR + Margin;
        bool FitsOnScreen(double shift)
        {
            for (double a = mid + shift - total / 2; a <= mid + shift + total / 2 + 0.01; a += 4)
            {
                double rad = a * Math.PI / 180;
                double x = reach * Math.Sin(rad), y = -reach * Math.Cos(rad);
                if (x < -_room.Left || x > _room.Right || y < -_room.Top || y > _room.Bottom)
                    return false;
            }
            return true;
        }
        for (double shift = 0; shift <= 180; shift += 3)
        {
            if (FitsOnScreen(shift))
                return shift;
            if (shift > 0 && FitsOnScreen(-shift))
                return -shift;
        }
        return 0; // nowhere fully on screen (tiny work area): keep it centered
    }

    double ArrowHalfWidth(int level) =>
        level == 0 ? Math.Min(_span / 2 - 7, 15) : Math.Min(_bands[level].Span / 2 - 5, 10);

    void AddArrow(int level, Tile tile)
    {
        tile.Arrow = new Path
        {
            Data = ArrowGeometry(tile.Mid, ArrowHalfWidth(level), _bands[level].EdgeR),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _bands[level].Layer.Children.Add(tile.Arrow);
        // Fades in to whatever opacity Paint gives it (the animation stops, it does not hold the value).
        tile.Arrow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, AnimationTime(160)) { FillBehavior = FillBehavior.Stop });
    }

    // ---- inline input ----

    /// <summary>
    /// Replaces the ring with a card in the menu center that asks for a folder name and, for
    /// "New + Prompt", then a prompt; or, for Ask, only a prompt. Validation is shown inline as the user types.
    /// </summary>
    internal void StartInput(InputSpec spec)
    {
        if (!_isOpen)
            return;
        _input = spec;
        _promptStage = false;
        SetHover(-1, -1);
        CloseFrom(1);
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
        var grow = new DoubleAnimation(Animate ? 0.9 : 1, 1, duration) { EasingFunction = ease };
        InputScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        InputScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        TakeFocus();
        if (spec.AskName)
            ShowNameStage();
        else
            ShowPromptStage(name: null);
    }

    void ResetInputMode()
    {
        _input = null;
        InputShift.X = InputShift.Y = 0;
        ResetNewProject();
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

    /// <param name="name">The accepted folder name, or null when only a prompt is asked for.</param>
    void ShowPromptStage(string? name)
    {
        var input = _input!;
        _promptStage = true;
        _acceptedName = name ?? "";
        InputTitle.Text = name is null ? input.Title : $"{input.Title} · {name}";
        InputLocation.Text = name is null ? input.Location ?? input.ParentDir : System.IO.Path.Combine(input.ParentDir, name);
        NameBox.Visibility = Visibility.Collapsed;
        PromptBox.Visibility = Visibility.Visible;
        AlignPlaceholder(PromptBox);
        SetMessage(Loc.Get(input.AskName ? "Input.HintPrompt" : "Input.HintPromptOnly"), isError: false);
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
        string hint = _newProject is not null ? NewProjectNameHint() : Loc.Get(input.AskPrompt ? "Input.HintNameNext" : "Input.HintName");
        SetMessage(error ?? hint, error is not null);
    }

    void SetMessage(string text, bool isError)
    {
        InputMessage.Text = text;
        InputMessage.Foreground = isError ? _palette.Error : _palette.SubText;
    }

    void OnInputKeyDown(KeyEventArgs e)
    {
        if (_newProject is not null)
        {
            OnNewProjectKeyDown(e);
            return;
        }
        var input = _input!;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_promptStage && input.AskName)
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
        CloseFrom(1);
        SliceLayer.Children.Clear();
        Slices.Clear();
        _bands[0].Opened = -1;
        (_hoverLevel, _hoverIndex) = (-1, -1);
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
            PlaceLabel(tile, mid, maxLabelWidth, LabelR, TileInnerR, TileOuterR, _span, _bands[0].FontSizes);
            Slices.Add(tile);
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
    /// Centers a tile's icon + label on its middle angle, using the widest width (≤ max) at which the measured
    /// block stays inside its band and its own wedge. Narrower widths wrap the label onto a second line,
    /// which matters for the left/right slices where the ring is only ~100 DIP deep.
    /// </summary>
    /// <param name="folderName">
    /// The label is a folder name, often one long word whose last part is what tells it apart (….Modules.Auth,
    /// ….Modules.Web). The whole name is shown if any font size allows it: unbroken if possible, else wrapped at
    /// its camel-case and separator boundaries. Then come shorter forms that keep its start and end, and only
    /// then is its end trimmed like any other label's.
    /// </param>
    void PlaceLabel(Tile tile, double mid, double maxWidth, double labelR, double innerR, double outerR, double span, double[] fontSizes,
        bool folderName = false)
    {
        var (block, label) = (tile.Content, tile.Label);
        string text = label.Text;
        var key = (text, tile.Hint.Visibility == Visibility.Collapsed ? "" : tile.Hint.Text, Math.Round(mid, 1), span, labelR);
        if (_labelLayouts.TryGetValue(key, out var known))
        {
            label.Text = known.Text;
            label.FontSize = known.FontSize;
            block.Width = known.Size.Width;
            Position(block, known.Size, OnCircle(labelR, mid));
            return;
        }

        bool trimmed = false;
        Size? TryWidth(double width, bool whole, double slack)
        {
            block.Width = width;
            trimmed = false;
            if (whole)
            {
                // Measured without the height limit: a taller label would be trimmed.
                double maxHeight = label.MaxHeight;
                label.MaxHeight = double.PositiveInfinity;
                block.Measure(new Size(width, double.PositiveInfinity));
                label.MaxHeight = maxHeight;
                trimmed = label.DesiredSize.Height > maxHeight + 0.5;
                if (trimmed)
                    return null;
            }
            else
            {
                block.Measure(new Size(width, double.PositiveInfinity));
            }
            var size = new Size(width, block.DesiredSize.Height);
            bool fits = folderName && block is Panel rows
                ? RowsFit(rows, mid, size, labelR, innerR, outerR, span, slack)
                : Fits(mid, size, labelR, innerR, outerR, span);
            return fits ? size : null;
        }

        Size last = default;
        bool Search(bool whole, double slack = 0)
        {
            foreach (double fontSize in fontSizes)
            {
                label.FontSize = fontSize;
                // Never narrower than the longest word, so words are not broken in the middle.
                double longestWord = LongestWordWidth(label) + 2;
                if (whole && longestWord > maxWidth)
                    continue; // a word would break in the middle
                double minWidth = Math.Clamp(longestWord, MinLabelWidth, maxWidth);
                // Steps of 4 down from the widest width, then one step back up: half the measurements of going down
                // by 2, and fitting is not monotonic (a narrower, taller block can fail where a wider one fits), so
                // the steps stay small enough not to jump over a width that fits.
                for (double width = maxWidth; ; width = Math.Max(minWidth, NextWidth(width)))
                {
                    if (TryWidth(width, whole, slack) is { } fit)
                    {
                        for (double wider = Math.Min(maxWidth, width + 2); wider > width; wider -= 2)
                        {
                            if (TryWidth(wider, whole, slack) is { } better)
                            {
                                fit = better;
                                break;
                            }
                        }
                        block.Width = fit.Width;
                        _labelLayouts[key] = (label.Text, fontSize, fit);
                        Position(block, fit, OnCircle(labelR, mid));
                        return true;
                    }
                    last = new Size(width, block.DesiredSize.Height);
                    if (width <= minWidth || trimmed)
                        break; // narrower is only taller: trimmed as well
                }
            }
            return false;
        }

        // A folder name's rows are checked at their own width, so every width down to its widest line wraps it the
        // same way and gives the same answer: those are skipped.
        double NextWidth(double width) =>
            folderName ? Math.Min(width - 4, Math.Floor(label.DesiredSize.Width) - 1) : width - 4;

        if (folderName)
        {
            string breakable = BreakableName(text);
            foreach (string candidate in new[] { text, breakable }.Concat(MiddleShortened(breakable)).Distinct())
            {
                label.Text = candidate;
                // The unbroken name may come a little closer to the edges: the corners of its line are empty.
                if (Search(whole: true, slack: candidate == text ? UnbrokenSlack : 0))
                    return;
            }
            label.Text = breakable; // trimmed, but still not broken in the middle of a word
        }
        if (Search(whole: false))
            return;
        block.Width = last.Width;
        Position(block, last, OnCircle(labelR, mid)); // best effort with the smallest size tried
    }

    /// <summary>
    /// Shorter forms of a long folder name (made breakable by <see cref="BreakableName"/>) that keep its start and
    /// its end: "Contoso.Platform.Modules.CCTV" → "Contoso…Modules.CCTV", then "Contoso…CCTV".
    /// </summary>
    static IEnumerable<string> MiddleShortened(string name)
    {
        string[] parts = name.Split('\u200B');
        string first = parts[0].TrimEnd('.', '_', '-') is { Length: > 0 } stem ? stem : parts[0];
        for (int keep = Math.Min(2, parts.Length - 2); keep >= 1; keep--)
            yield return first + "…\u200B" + string.Join('\u200B', parts[^keep..]);
    }

    static void Position(FrameworkElement block, Size size, Point center)
    {
        Canvas.SetLeft(block, center.X - size.Width / 2);
        Canvas.SetTop(block, center.Y - size.Height / 2);
    }

    static double LongestWordWidth(TextBlock text)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        return text.Text.Split([' ', '​'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new FormattedText(word, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, text.FontSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace)
            .DefaultIfEmpty(0)
            .Max();
    }

    static bool Fits(double mid, Size size, double labelR, double innerR, double outerR, double span) =>
        RectFits(OnCircle(labelR, mid), size, mid, innerR, outerR, span, slack: 0);

    /// <summary>
    /// Like <see cref="Fits"/>, but each row of the block (label, hint) is checked with its own width instead of the
    /// block's bounding box: a short hint under a long name leaves the bottom corners empty.
    /// </summary>
    /// <param name="slack">How much closer to the edges than <see cref="Fits"/> allows the rows may come, in DIP.</param>
    static bool RowsFit(Panel block, double mid, Size size, double labelR, double innerR, double outerR, double span, double slack)
    {
        var center = OnCircle(labelR, mid);
        double top = center.Y - size.Height / 2;
        foreach (UIElement row in block.Children)
        {
            if (row.Visibility != Visibility.Visible)
                continue;
            var rowSize = row.DesiredSize;
            if (!RectFits(new Point(center.X, top + rowSize.Height / 2), rowSize, mid, innerR, outerR, span, slack))
                return false;
            top += rowSize.Height;
        }
        return true;
    }

    static bool RectFits(Point center, Size size, double mid, double innerR, double outerR, double span, double slack)
    {
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            double dx = center.X + sx * size.Width / 2 - StageR;
            double dy = center.Y + sy * size.Height / 2 - StageR;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            if (radius < innerR + 2 - slack || radius > outerR - RimWidth - 1 + slack)
                return false;
            if (span < 360)
            {
                double angle = Math.Atan2(dx, -dy) * 180 / Math.PI;
                if (Math.Abs(AngleBetween(mid, angle)) > span / 2 - 3 + slack * 180 / (Math.PI * radius))
                    return false;
            }
        }
        return true;
    }

    bool IsHot(int level, int index) => level == _hoverLevel && index == _hoverIndex;

    /// <summary>
    /// Selects a tile (or nothing with -1). The bands it belongs to stay open, and so does the arc the tile
    /// opened itself; everything further out closes, and a tile with items opens them. Selecting nothing
    /// (the pointer left the menu, or rests in the center) keeps everything open, so the pointer can travel.
    /// </summary>
    void SetHover(int level, int index)
    {
        if (level == _hoverLevel && index == _hoverIndex)
            return;
        var (previousLevel, previousIndex) = (_hoverLevel, _hoverIndex);
        (_hoverLevel, _hoverIndex) = level >= 0 && index >= 0 ? (level, index) : (-1, -1);
        if (previousLevel >= 0 && previousIndex < _bands[previousLevel].Tiles.Count)
            Paint(_bands[previousLevel].Tiles[previousIndex], hot: false, _bands[previousLevel].Opened == previousIndex);
        if (_hoverLevel < 0)
            return;

        var band = _bands[level];
        if (band.Opened == index)
        {
            CloseFrom(level + 2); // back on Recent Projects: its projects stay, a project's sessions close
        }
        else
        {
            CloseFrom(level + 1);
            if (band.Tiles[index].Children.Count > 0 && level + 1 < _bands.Length)
                Open(level, index);
        }
        Paint(band.Tiles[index], hot: true, band.Opened == index);
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
        GrandShadow.Opacity = _palette.ShadowOpacity * 0.8;
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
        ApplyNewProjectPalette();
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

    /// <summary>
    /// A short band just outside <paramref name="baseR"/> whose outer edge comes to a point: "this tile opens outward".
    /// </summary>
    static Geometry ArrowGeometry(double mid, double halfWidth, double baseR)
    {
        double inner = baseR + 5, outer = baseR + 8, tip = baseR + 15;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(OnCircle(inner, mid - halfWidth), isFilled: true, isClosed: true);
            ctx.ArcTo(OnCircle(inner, mid + halfWidth), new Size(inner, inner), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(OnCircle(outer, mid + halfWidth), true, true);
            ctx.LineTo(OnCircle(tip, mid), true, true);
            ctx.LineTo(OnCircle(outer, mid - halfWidth), true, true);
        }
        geometry.Freeze();
        return geometry;
    }
}
