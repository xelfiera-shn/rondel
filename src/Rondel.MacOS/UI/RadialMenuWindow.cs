using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Rondel.Core.Agents;
using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Localization;
using Rondel.Core.Menus;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;
using Rondel.Core.Themes;
using Rondel.MacOS.Interop;
using Path = Avalonia.Controls.Shapes.Path;

namespace Rondel.MacOS.UI;

/// <summary>
/// Borderless, transparent, topmost radial menu, the counterpart of the Windows RadialMenuWindow, with the same
/// geometry, themes and label fitting. Created once at startup and only moved and shown afterwards, so it appears
/// without delay. All geometry is in points around the stage center.
/// <para>
/// The ring is made of separate tiles. The hovered tile lights up its rim in the brand gradient. A tile that opens
/// further (Resume into sessions, Recent Projects into folders, a folder into its sessions) carries an arrow outside
/// its band; when it is hovered, its items open on the next band out, an arc centered on it. The ring is band 0,
/// every arc one band further out.
/// </para>
/// <para>
/// It is a non-activating panel (<see cref="MenuPanel"/>): it takes the keyboard while Finder stays the active app.
/// Fully transparent pixels still catch the pointer on macOS (the window draws with Metal), so a press on the empty
/// stage closes the menu, as a click outside it does.
/// </para>
/// <para>
/// When an action needs a folder name or a prompt, the ring fades and the window shows the <see cref="InputCard"/>,
/// growing (and moving onto the screen) as far as the card needs.
/// </para>
/// </summary>
internal sealed class RadialMenuWindow : Window
{
    const double StageR = 410;         // the stage is 2 × this; the ring center is its center
    const double OuterR = 168;         // ring radius
    const double HubR = 52;            // hub disc radius
    const double TileInnerR = HubR + 7;
    const double TileOuterR = OuterR - 4;
    const double RimWidth = 4;         // room kept for a lit strip along a tile's outer edge (the same for every theme)
    const double DeadZoneR = 58;       // closer than this to the center selects nothing
    const double LabelR = 113;         // where icon + label blocks are centered
    const double ArcGap = 26;          // from a band's edge to the tiles of the arc it opens (room for the arrow)
    const double ArrowReach = 17;      // how far beyond its band's edge an arrow (and its hit area) reaches
    const double MinLabelWidth = 56;
    const double UnbrokenSlack = 4;    // how much closer to the edges an unbroken folder name may come
    const double ShadowPad = 32;       // shadow extent kept on screen when clamping to the work area

    /// <summary>One drawn tile: a slice of the ring or an item on an arc.</summary>
    sealed class Tile
    {
        public required MenuItemModel Item { get; init; }
        public required Path Fill { get; init; }
        public required Path Rim { get; init; }
        public required StackPanel Content { get; init; }
        public required Rectangle? Glyph { get; init; }
        public required TextBlock Label { get; init; }
        public required TextBlock Hint { get; init; }

        /// <summary>The fill of the highlighted tile (a wash, the accent, or a glow, depending on the theme).</summary>
        public required IBrush Wash { get; init; }

        /// <summary>The fill of a tile whose items are open while the pointer is elsewhere (glow themes).</summary>
        public required IBrush WashSoft { get; init; }

        public required IBrush Lit { get; init; }
        public required double Mid { get; init; }
        public bool Dimmed { get; set; }
        public Path? Arrow { get; set; }
        public IReadOnlyList<MenuItemModel> Children { get; set; } = [];
    }

    /// <summary>
    /// One band of tiles. Band 0 is the ring; band n + 1 is the arc opened from a tile of band n, centered on that
    /// tile (or turned towards the free side near a screen edge).
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

    readonly Panel _surface = new() { RenderTransformOrigin = RelativePoint.Center };
    readonly ScaleTransform _surfaceScale = new(1, 1);
    readonly Canvas _stage = new() { Width = 2 * StageR, Height = 2 * StageR, Background = Brushes.Transparent };
    readonly Ellipse _ringBase = new() { Width = 2 * OuterR, Height = 2 * OuterR, StrokeThickness = 1 };
    readonly DropShadowEffect _ringShadow = new() { BlurRadius = 30, OffsetY = 6, Color = Colors.Black, Opacity = 0.4 };
    readonly Ellipse _hubRing = new() { Width = 114, Height = 114, StrokeThickness = 1.6, IsHitTestVisible = false };
    readonly Ellipse _hub = new() { Width = 2 * HubR, Height = 2 * HubR, IsHitTestVisible = false };
    readonly StackPanel _center = new() { Width = 88, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    readonly Rectangle _centerGlyph = new() { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
    readonly TextBlock _centerTitle = new()
    {
        FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34,
    };
    readonly TextBlock _centerSubtitle = new() { FontSize = 10.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
    readonly Band[] _bands;
    readonly InputCard _card;

    /// <summary>
    /// Label layouts already worked out while this menu is open: arcs open again and again as the pointer moves
    /// between tiles, and fitting a label takes many measurements.
    /// </summary>
    readonly Dictionary<(string Label, string Hint, double Mid, double Span, double Radius), (string Text, double FontSize, Size Size)> _labelLayouts = [];

    MenuPalette _palette;
    MenuModel? _model;
    nint _nsWindow;
    bool _nonActivating;
    Path? _orbit;
    double _span = 360, _offset;
    int _hoverLevel = -1, _hoverIndex = -1;
    bool _isOpen, _pressedInside;
    int _version;
    CGPoint _centerOnScreen; // the ring center in screen points
    PixelRect _work;         // the work area of the screen the menu is on

    /// <summary>Room around the ring center up to the work area (points): left, top, right, bottom.</summary>
    (double Left, double Top, double Right, double Bottom) _room = (double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);

    public RadialMenuWindow(MenuPalette palette)
    {
        _palette = palette;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        // The system font: Avalonia's own default (Inter) draws Ğ İ Ü Ö without their marks.
        FontFamily = new FontFamily(".AppleSystemUIFont");

        _bands =
        [
            new Band { Layer = Layer(null), InnerR = TileInnerR, OuterR = TileOuterR, EdgeR = OuterR, TileSpan = 0, LabelMaxHeight = 34, FontSizes = [12] },
            // Session titles and folder names: up to three lines, slightly smaller if needed.
            new Band
            {
                Layer = Layer(new ScaleTransform(1, 1)), InnerR = OuterR + ArcGap, OuterR = OuterR + ArcGap + 82, EdgeR = OuterR + ArcGap + 86,
                TileSpan = 34, LabelMaxHeight = 46, FontSizes = [11.5, 11, 10.5],
            },
            // A recent project's sessions: a narrower band further out.
            new Band
            {
                Layer = Layer(new ScaleTransform(1, 1)), InnerR = OuterR + ArcGap + 86 + ArcGap, OuterR = OuterR + ArcGap + 86 + ArcGap + 76,
                EdgeR = OuterR + ArcGap + 86 + ArcGap + 80, TileSpan = 24, LabelMaxHeight = 46, FontSizes = [11, 10.5, 10],
            },
        ];
        foreach (var band in _bands.Skip(1))
            band.Layer.RenderTransform = band.Scale;

        _ringBase.Effect = _ringShadow;
        _center.Children.Add(_centerGlyph);
        _center.Children.Add(_centerTitle);
        _center.Children.Add(_centerSubtitle);
        foreach (var (element, radius) in new (Control, double)[] { (_ringBase, OuterR), (_hubRing, 57), (_hub, HubR) })
        {
            Canvas.SetLeft(element, StageR - radius);
            Canvas.SetTop(element, StageR - radius);
        }
        _stage.Children.Add(_ringBase);
        _stage.Children.Add(_bands[0].Layer);
        _stage.Children.Add(_hubRing);
        _stage.Children.Add(_hub);
        // The center's content is centered on the hub whatever its height (a title on one or two lines).
        var hubContent = new Panel { Width = 2 * HubR, Height = 2 * HubR, IsHitTestVisible = false, Children = { _center } };
        _center.HorizontalAlignment = HorizontalAlignment.Center;
        Canvas.SetLeft(hubContent, StageR - HubR);
        Canvas.SetTop(hubContent, StageR - HubR);
        _stage.Children.Add(hubContent);
        _stage.Children.Add(_bands[1].Layer);
        _stage.Children.Add(_bands[2].Layer);
        _card = new InputCard(palette);
        _card.SizeChanged += (_, _) => FitCard();
        _surface.RenderTransform = _surfaceScale;
        _surface.Children.Add(new Canvas
        {
            Width = 2 * StageR, Height = 2 * StageR, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Children = { _stage },
        });
        _surface.Children.Add(_card);
        Content = _surface;

        ApplyPalette();
        PointerMoved += (_, e) => OnPointer(e.GetPosition(_stage), pressed: false, released: false);
        PointerExited += (_, _) => { if (_isOpen && !_card.IsActive) SetHover(-1, -1); };
        PointerPressed += (_, e) => OnPointer(e.GetPosition(_stage), pressed: true, released: false);
        PointerReleased += (_, e) => OnPointer(e.GetPosition(_stage), pressed: false, released: true);
        KeyDown += OnKeyDown;
        // Losing the key window (another Space, ⌘Tab, a click in another app) closes the menu, as a click outside it does.
        Deactivated += (_, _) => CloseMenu(restoreFocus: false);
    }

    /// <summary>An arc's layer: it fades and grows in from the ring's center, and casts its own shadow.</summary>
    static Canvas Layer(ScaleTransform? scale) => new()
    {
        Width = 2 * StageR,
        Height = 2 * StageR,
        IsHitTestVisible = false,
        RenderTransformOrigin = RelativePoint.Center,
        Effect = scale is null ? null : new DropShadowEffect { BlurRadius = 22, OffsetY = 4, Color = Colors.Black, Opacity = 0.35 },
    };

    /// <summary>A slice or an item on an arc was chosen (click, release over it, number key, Enter).</summary>
    public event Action<MenuModel, MenuItemModel>? ItemInvoked;

    /// <summary>The menu closed; the frame it had (screen points, top-left origin) is no longer Rondel's.</summary>
    public event Action? MenuClosed;

    /// <summary>The window grew or moved for the input card; presses outside the new frame close the menu.</summary>
    public event Action<CGRect>? FrameChanged;

    public bool IsMenuOpen => _isOpen;

    List<Tile> Slices => _bands[0].Tiles;

    /// <summary>
    /// Creates the native window and draws a menu once, invisibly and on screen (off screen the window came back
    /// 0×0), so the first real open is instant. Every band is warmed up.
    /// </summary>
    public void Prepare()
    {
        var agent = BuiltInAgents.ClaudeCode();
        var warmup = AppSettings.DefaultActions().Take(4).Select(a => new MenuItemModel(a, agent, a.Id, a.Glyph, "warm-up", true)).ToList();
        var nested = warmup.Select((item, i) => i == 0 ? item with { Children = warmup } : item).ToList();
        _model = new MenuModel(TriggerContext.None("warm-up"), "warm-up", "warm-up", warmup);
        Width = Height = 2 * StageR;
        BuildSlices(_model);
        ApplyChildren(_model, [.. warmup.Select((_, i) => i == 0 ? (IReadOnlyList<MenuItemModel>)nested : [])], force: true);
        SetHover(0, 0);
        SetHover(1, _bands[1].PositionOfRank[0]);
        SetHover(-1, -1);
        Position = new PixelPoint(0, 0);
        Opacity = 0;
        Show();
        _nsWindow = TryGetPlatformHandle()?.Handle ?? 0;
        _nonActivating = MenuPanel.Configure(_nsWindow);
        Hide();
        Opacity = 1;
        CloseFrom(1);
    }

    /// <summary>Opens the menu centered on <paramref name="anchor"/>, keeping the ring inside the work area of its screen.</summary>
    /// <returns>The window's frame in screen points, for the event tap.</returns>
    public CGRect ShowMenu(MenuModel model, CGPoint anchor)
    {
        _version++;
        _model = model;
        _pressedInside = false;
        _labelLayouts.Clear();
        _card.Reset();

        // Center on the pointer, but keep the ring and its shadow inside the screen's work area; arcs turn away from edges.
        var screen = Screens.ScreenFromPoint(new PixelPoint((int)anchor.X, (int)anchor.Y)) ?? Screens.Primary;
        var work = _work = screen?.WorkingArea ?? new PixelRect(0, 0, 10_000, 10_000);
        double half = OuterR + ShadowPad;
        double cx = Clamp(anchor.X, work.X + half, work.Right - half), cy = Clamp(anchor.Y, work.Y + half, work.Bottom - half);
        _centerOnScreen = new CGPoint(cx, cy);
        _room = (cx - work.X, cy - work.Y, work.Right - cx, work.Bottom - cy);

        BuildSlices(model);
        SetCenterTitle(model.Title);
        _centerSubtitle.Text = model.Subtitle ?? "";
        _centerSubtitle.IsVisible = !string.IsNullOrEmpty(model.Subtitle);
        SetSymbol(_centerGlyph, CenterSymbolFor(model.Context.Kind), 18, _palette.Brand);

        // The window is only as big as the deepest arc this menu can open; the stage stays centered in it.
        double size = StageSizeFor(model);
        Width = Height = size;
        Position = new PixelPoint((int)Math.Round(cx - size / 2), (int)Math.Round(cy - size / 2));

        _stage.Opacity = 1;
        _surface.Opacity = 0;
        _surfaceScale.ScaleX = _surfaceScale.ScaleY = Animate ? 0.86 : 1;
        MenuPanel.BeforeShow(_nsWindow, _nonActivating);
        ShowActivated = !_nonActivating;
        Show();
        MenuPanel.TakeKey(_nsWindow, _nonActivating);
        _isOpen = true;
        FadeIn(_surface, _surfaceScale, 160, 0.86);
        return Frame;
    }

    CGRect Frame => new() { X = Position.X, Y = Position.Y, Width = Width, Height = Height };

    /// <summary>Replaces the ring with the card that asks for a folder name (and a prompt), or a prompt alone.</summary>
    public void StartInput(InputSpec spec)
    {
        if (!_isOpen)
            return;
        SetHover(-1, -1);
        CloseFrom(1);
        _stage.Opacity = 0;
        _card.Start(spec, CloseMenu);
    }

    /// <summary>Replaces the ring with the New Project card.</summary>
    public void StartNewProject(NewProjectSpec spec)
    {
        if (!_isOpen)
            return;
        SetHover(-1, -1);
        CloseFrom(1);
        _stage.Opacity = 0;
        _card.Start(spec, CloseMenu);
    }

    /// <summary>
    /// The card can be taller than the ring (the New Project card with its prompt): the window grows around the
    /// ring's center as far as the card needs, and moves just far enough to stay inside the work area.
    /// </summary>
    void FitCard()
    {
        if (!_isOpen || !_card.IsActive)
            return;
        double width = Math.Max(Width, _card.Bounds.Width + 2 * ShadowPad);
        double height = Math.Max(Height, _card.Bounds.Height + 2 * ShadowPad);
        double left = Clamp(_centerOnScreen.X - width / 2, _work.X, _work.Right - width);
        double top = Clamp(_centerOnScreen.Y - height / 2, _work.Y, _work.Bottom - height);
        if (width == Width && height == Height && (int)Math.Round(left) == Position.X && (int)Math.Round(top) == Position.Y)
            return;
        Width = width;
        Height = height;
        Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
        FrameChanged?.Invoke(Frame);
    }

    /// <summary>The pointer moved while the button that opened the menu is still down (from the event tap).</summary>
    public void OnHeldMove(CGPoint point)
    {
        if (_isOpen && !_card.IsActive)
            SetHover(HitTest(point.X - _centerOnScreen.X, point.Y - _centerOnScreen.Y));
    }

    /// <summary>The button that opened the menu was released at <paramref name="point"/> (from the event tap).</summary>
    public void OnHeldRelease(CGPoint point)
    {
        if (!_isOpen || _card.IsActive)
            return;
        var (level, index) = HitTest(point.X - _centerOnScreen.X, point.Y - _centerOnScreen.Y);
        if (level >= 0)
            Invoke(level, index);
    }

    public void CloseMenu(bool restoreFocus)
    {
        if (!_isOpen)
            return;
        _isOpen = false;
        int version = ++_version;
        SetHover(-1, -1);
        MenuClosed?.Invoke();
        MenuPanel.AfterClose(_nonActivating, restoreFocus);
        _surface.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (_version != version)
                return;
            Hide();
            CloseFrom(1);
        }, TimeSpan.FromMilliseconds(Animate ? 110 : 0));
    }

    /// <summary>Switches to another theme or mode; an open menu is redrawn with what it shows.</summary>
    public void ApplyLook(MenuPalette palette)
    {
        _palette = palette;
        ApplyPalette();
        _card.ApplyPalette(palette);
        if (!_isOpen || _model is null || _card.IsActive)
            return;
        // Rebuild with the new colors, keeping what arrived after the menu opened and what is open.
        var hints = Slices.Select(s => (s.Hint.IsVisible && s.Hint.Opacity > 0 ? s.Hint.Text : null, s.Dimmed)).ToList();
        var children = Slices.Select(s => s.Children).ToList();
        var opened = _bands.Select(b => b.Opened).ToList();
        BuildSlices(_model);
        ApplyHints(_model, hints);
        ApplyChildren(_model, children);
        for (int level = 0; level < _bands.Length - 1 && opened[level] >= 0 && opened[level] < _bands[level].Tiles.Count; level++)
            Open(level, opened[level]);
    }

    /// <summary>
    /// Late session hints (looked up off the main thread after the menu opened). Ignored if the menu has moved on
    /// to another context meanwhile.
    /// </summary>
    public void ApplyHints(MenuModel model, IReadOnlyList<(string? Hint, bool Dimmed)> hints)
    {
        if (!ReferenceEquals(model, _model) || hints.Count != Slices.Count)
            return;
        for (int i = 0; i < hints.Count; i++)
        {
            var slice = Slices[i];
            if (hints[i].Hint is not { } text)
                continue;
            slice.Hint.Text = text;
            slice.Hint.IsVisible = true;
            slice.Hint.Opacity = 1;
            slice.Dimmed = hints[i].Dimmed;
            Paint(slice, IsHot(0, i), _bands[0].Opened == i);
        }
    }

    /// <summary>
    /// Late items for slices that open further (recent sessions, recent projects). A slice with items gets its
    /// arrow; if it is already hovered, it opens right away.
    /// </summary>
    public void ApplyChildren(MenuModel model, IReadOnlyList<IReadOnlyList<MenuItemModel>> children, bool force = false)
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

    /// <summary>
    /// Developer aid (--render-menu): draws the menu for <paramref name="model"/> into a bitmap at 2x, without showing
    /// the window, on a solid background so the shadows read as they would on screen.
    /// </summary>
    /// <param name="path">What to hover: a slice index, then for each arc the rank of an item (0 = newest).</param>
    /// <param name="atLeftEdge">As if the menu opened at the left edge of the work area (the ring clamped against it).</param>
    public Bitmap RenderPreview(MenuModel model, IReadOnlyList<(string? Hint, bool Dimmed)> hints,
        IReadOnlyList<IReadOnlyList<MenuItemModel>> children, IReadOnlyList<int> path, Color background, bool atLeftEdge = false)
    {
        _room = atLeftEdge
            ? (OuterR + ShadowPad, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity)
            : (double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        _model = model;
        _labelLayouts.Clear();
        BuildSlices(model);
        SetCenterTitle(model.Title);
        _centerSubtitle.Text = model.Subtitle ?? "";
        _centerSubtitle.IsVisible = !string.IsNullOrEmpty(model.Subtitle);
        SetSymbol(_centerGlyph, CenterSymbolFor(model.Context.Kind), 18, _palette.Brand);
        ApplyHints(model, hints);
        ApplyChildren(model, children, force: true);
        if (path.Count > 0)
            SetHover(0, path[0]);
        for (int level = 1; level < path.Count && _bands[level].Tiles.Count > 0; level++)
            SetHover(level, _bands[level].PositionOfRank[Math.Min(path[level], _bands[level].Tiles.Count - 1)]);
        foreach (var band in _bands)
            band.Layer.Opacity = 1;
        _surface.Opacity = 1;
        double size = StageSizeFor(model);
        var root = new Panel { Width = size, Height = size, Background = new SolidColorBrush(background) };
        Content = null;
        root.Children.Add(_surface);
        root.Measure(new Size(size, size));
        root.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(new PixelSize((int)(size * 2), (int)(size * 2)), new Vector(192, 192));
        bitmap.Render(root);
        root.Children.Clear();
        Content = _surface;
        return bitmap;
    }

    /// <summary>Developer aid (--render-new): draws the New Project card into a bitmap at 2x without showing the window.</summary>
    public Bitmap RenderNewProjectPreview(NewProjectSpec spec, string name, string? prompt, Color background)
    {
        _card.Start(spec, _ => { });
        _card.Fill(name, prompt);
        const double Size = 540;
        var root = new Panel { Width = Size, Height = Size, Background = new SolidColorBrush(background) };
        _surface.Children.Remove(_card);
        root.Children.Add(_card);
        Content = root; // in the window's tree, so the text boxes get their templates
        // Text boxes settle their scroll viewers over several layout passes and posted jobs.
        for (int pass = 0; pass < 3; pass++)
        {
            Width = Height = Size;
            Measure(new Size(Size, Size));
            Arrange(new Rect(0, 0, Size, Size));
            Dispatcher.UIThread.RunJobs();
        }
        var bitmap = new RenderTargetBitmap(new PixelSize((int)(Size * 2), (int)(Size * 2)), new Vector(192, 192));
        bitmap.Render(root);
        root.Children.Clear();
        _surface.Children.Add(_card);
        Content = _surface;
        return bitmap;
    }

    // ---- input ----

    void OnPointer(Point stagePoint, bool pressed, bool released)
    {
        if (!_isOpen)
            return;
        if (_card.IsActive)
        {
            // Around the card the window is transparent but still Rondel's: a press there is a click outside.
            if (pressed && !_card.IsPointerOver)
                CloseMenu(restoreFocus: true);
            return;
        }
        double dx = stagePoint.X - StageR, dy = stagePoint.Y - StageR;
        var (level, index) = HitTest(dx, dy);
        if (pressed)
        {
            // Transparent pixels catch the pointer on macOS: a press on the empty stage is a click outside the menu.
            if (level < 0 && Math.Sqrt(dx * dx + dy * dy) > OuterR + ArrowReach)
            {
                CloseMenu(restoreFocus: true);
                return;
            }
            _pressedInside = true;
            return;
        }
        if (released)
        {
            bool wasPressed = _pressedInside;
            _pressedInside = false;
            if (wasPressed && level >= 0)
                Invoke(level, index);
            return;
        }
        SetHover(level, index);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isOpen || Slices.Count == 0 || _card.IsActive)
            return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        int number = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
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
        switch (e.Key)
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

    /// <summary>The band and tile under an offset from the ring center (points); (-1, -1) for none.</summary>
    (int Level, int Index) HitTest(double dx, double dy)
    {
        if (Slices.Count == 0)
            return (-1, -1);
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Atan2(dx, -dy) * 180 / Math.PI; // 0° = up, clockwise

        // Open arcs, outermost first. Each has its tiles (with the arrows of tiles that open further) and, between
        // it and the band it was opened from, a bridge that keeps the opening tile selected.
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

    bool IsHot(int level, int index) => level == _hoverLevel && index == _hoverIndex;

    void SetHover((int Level, int Index) hit) => SetHover(hit.Level, hit.Index);

    /// <summary>
    /// Selects a tile (or nothing with -1). The bands it belongs to stay open, and so does the arc the tile opened
    /// itself; everything further out closes, and a tile with items opens them. Selecting nothing (the pointer left
    /// the menu, or rests in the center) keeps everything open, so the pointer can travel.
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

    // ---- slices ----

    /// <summary>
    /// The window is only as big as the deepest arc this menu can open (none, sessions, or recent projects and their
    /// sessions): every frame of a transparent window is drawn whole, so size is speed.
    /// </summary>
    double StageSizeFor(MenuModel model)
    {
        int depth = model.Items.Any(MenuBuilder.ListsRecentProjects) ? 2 : model.Items.Any(MenuBuilder.CanExpand) ? 1 : 0;
        double reach = (depth == 0 ? OuterR : _bands[depth].EdgeR) + ArrowReach + ShadowPad;
        return Math.Min(2 * StageR, Math.Ceiling(2 * reach));
    }

    void BuildSlices(MenuModel model)
    {
        CloseFrom(1);
        var ring = _bands[0];
        ring.Layer.Children.Clear();
        Slices.Clear();
        ring.Opened = -1;
        (_hoverLevel, _hoverIndex) = (-1, -1);
        int n = model.Items.Count;
        if (n == 0)
            return;

        _span = 360.0 / n;
        _offset = n == 2 ? -90 : 0; // two items read better left/right than top/bottom
        double maxLabelWidth = n <= 4 ? 104 : Math.Max(MinLabelWidth, 2 * LabelR * Math.Sin(Math.PI / n) - 8);
        // Session hints arrive after the menu opened; their line is reserved so labels don't move then.
        string hintSample = Loc.Format("Time.Minutes", 59);

        for (int i = 0; i < n; i++)
        {
            var item = model.Items[i];
            double mid = _offset + i * _span;
            var tile = CreateTile(item, mid, _span, TileInnerR, TileOuterR, ring.Layer, withGlyph: true);
            bool expectsHint = item.Action.Hint != SessionHint.None;
            tile.Hint.Text = item.Hint ?? (expectsHint ? hintSample : "");
            tile.Hint.IsVisible = item.Hint is not null || expectsHint;
            tile.Hint.Opacity = item.Hint is not null ? 1 : 0;
            tile.Dimmed = item.Dimmed;
            PlaceLabel(tile, mid, maxLabelWidth, LabelR, TileInnerR, TileOuterR, _span, ring.FontSizes);
            Slices.Add(tile);
            Paint(tile, hot: false, expanded: false);
        }
        _orbit = _palette.Theme.Decoration == RingDecoration.Orbit ? OrbitDecoration(n) : null;
        if (_orbit is not null)
            ring.Layer.Children.Add(_orbit);
    }

    /// <summary>A thin accent ring along the edge, broken at every slice by a short tick pointing inward.</summary>
    Path OrbitDecoration(int n)
    {
        const double R = OuterR - 3, GapDegrees = 7;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < n; i++)
            {
                double mid = _offset + i * _span;
                double from = mid + GapDegrees, to = mid + _span - GapDegrees;
                if (to > from)
                {
                    ctx.BeginFigure(OnCircle(R, from), isFilled: false);
                    ctx.ArcTo(OnCircle(R, to), new Size(R, R), 0, to - from > 180, SweepDirection.Clockwise);
                    ctx.EndFigure(isClosed: false);
                }
                ctx.BeginFigure(OnCircle(R + 3, mid), isFilled: false);
                ctx.LineTo(OnCircle(R - 9, mid));
                ctx.EndFigure(isClosed: false);
            }
        }
        return new Path
        {
            Data = geometry,
            Stroke = _palette.BrandBetween(new Point(StageR, StageR - OuterR), new Point(StageR, StageR + OuterR)),
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            Effect = _palette.Glow,
            IsHitTestVisible = false,
        };
    }

    Tile CreateTile(MenuItemModel item, double mid, double span, double innerR, double outerR, Canvas layer, bool withGlyph)
    {
        var theme = _palette.Theme;
        double from = mid - span / 2, to = mid + span / 2;
        double gap = span < 360 ? theme.TileGap : 0;
        var shape = theme.Tiles == TileShape.Rounded && span < 360
            ? RoundedTileGeometry(innerR, outerR, from, to, gap, theme.CornerRadius)
            : TileGeometry(innerR, outerR, from, to, gap);
        var fill = new Path { Data = shape, StrokeThickness = 1 };
        var rim = new Path { Data = TileGeometry(outerR - theme.RimWidth, outerR, from, to, gap) };
        layer.Children.Add(fill);
        layer.Children.Add(rim);

        Rectangle? glyph = null;
        if (withGlyph)
        {
            glyph = new Rectangle { Width = 22, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
            SetSymbol(glyph, SymbolIcons.NameFor(item.Glyph), 20, _palette.Text);
        }
        var label = new TextBlock
        {
            FontSize = 12, Text = item.Label, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34,
        };
        var hint = new TextBlock
        {
            FontSize = 10, Text = item.Hint ?? "", TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), IsVisible = item.Hint is not null,
        };
        var stack = new StackPanel();
        if (glyph is not null)
            stack.Children.Add(glyph);
        stack.Children.Add(label);
        stack.Children.Add(hint);
        layer.Children.Add(stack);

        // The rim's gradient runs along the arc; the wash deepens towards the outer edge.
        double half = Math.Min(span, 359) / 2;
        var (inner, outer) = (OnCircle(innerR, mid), OnCircle(outerR, mid));
        var center = OnCircle((innerR + outerR) / 2, mid);
        // A glow stays inside its own wedge, so neighbouring glows never touch.
        double glowR = Math.Min(Math.Min(56, (outerR - innerR) * 0.62), (innerR + outerR) / 2 * Math.Sin(Math.Min(span, 180) * Math.PI / 360) * 0.95);
        var (wash, washSoft) = theme.Highlight switch
        {
            HighlightStyle.Fill => (_palette.AccentFillBetween(inner, outer), _palette.TileFill),
            HighlightStyle.Outline => (_palette.HotFill, _palette.TileFill),
            HighlightStyle.Glow => (_palette.GlowAround(center, glowR, 1), _palette.GlowAround(center, glowR, 0.45)),
            _ => (_palette.WashBetween(inner, outer), _palette.TileFill),
        };
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
            Wash = wash,
            WashSoft = washSoft,
            Lit = _palette.BrandBetween(OnCircle(outerR, mid - half), OnCircle(outerR, mid + half)),
        };
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

        // A base under the items, so the gaps between them look like the ring's.
        band.Layer.Children.Add(new Path
        {
            Data = TileGeometry(band.InnerR - 4, band.EdgeR, band.From - 1.5, band.From + total + 1.5, 0),
            Fill = _palette.RingFill,
            Stroke = _palette.RingStroke,
            StrokeThickness = 1,
        });

        // Items arrive newest first. The newest takes the place closest to the tile (right outside it when the arc
        // is centered) and the next ones the next closest, alternating clockwise and counterclockwise. Near a screen
        // edge, where the arc had to turn, the newest is at the end next to the tile. Tiles are kept in position
        // order (clockwise).
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
            FadeIn(band.Layer, scale, 150, 0.94);
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
    /// turn away from being centered on <paramref name="mid"/> to stay inside the work area: 0 when it fits, else the
    /// smallest turn that fits (clockwise first). Near a screen edge the items open towards the free side.
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
            StrokeJoin = PenLineJoin.Round,
        };
        _bands[level].Layer.Children.Add(tile.Arrow);
    }

    // ---- labels ----

    /// <summary>
    /// Centers a tile's icon + label on its middle angle, using the widest width (≤ max) at which the measured block
    /// stays inside its band and its own wedge. Narrower widths wrap the label onto a second line, which matters for
    /// the left and right slices where the ring is only about 100 points deep.
    /// </summary>
    /// <param name="folderName">
    /// The label is a folder name, often one long word whose last part is what tells it apart (….Modules.Auth,
    /// ….Modules.Web). The whole name is shown if any font size allows it: unbroken if possible, else wrapped at its
    /// camel-case and separator boundaries. Then come shorter forms that keep its start and end, and only then is its
    /// end trimmed like any other label's.
    /// </param>
    void PlaceLabel(Tile tile, double mid, double maxWidth, double labelR, double innerR, double outerR, double span, double[] fontSizes,
        bool folderName = false)
    {
        var (block, label) = (tile.Content, tile.Label);
        string text = label.Text ?? "";
        var key = (text, tile.Hint.IsVisible ? tile.Hint.Text ?? "" : "", Math.Round(mid, 1), span, labelR);
        if (_labelLayouts.TryGetValue(key, out var known))
        {
            label.Text = known.Text;
            label.FontSize = known.FontSize;
            block.Width = known.Size.Width;
            PlaceAt(block, known.Size, OnCircle(labelR, mid));
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
            bool fits = folderName
                ? RowsFit(block, mid, size, labelR, innerR, outerR, span, slack)
                : RectFits(OnCircle(labelR, mid), size, mid, innerR, outerR, span, 0);
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
                // Steps of 4 down from the widest width, then one step back up: fitting is not monotonic (a narrower,
                // taller block can fail where a wider one fits), so the steps stay small enough not to jump over a fit.
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
                        _labelLayouts[key] = (label.Text ?? "", fontSize, fit);
                        PlaceAt(block, fit, OnCircle(labelR, mid));
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
        PlaceAt(block, last, OnCircle(labelR, mid)); // best effort with the smallest size tried
    }

    /// <summary>
    /// Shorter forms of a long folder name (made breakable by <see cref="BreakableName"/>) that keep its start and its
    /// end: "Contoso.Platform.Modules.CCTV" → "Contoso…Modules.CCTV", then "Contoso…CCTV".
    /// </summary>
    static IEnumerable<string> MiddleShortened(string name)
    {
        string[] parts = name.Split('​');
        string first = parts[0].TrimEnd('.', '_', '-') is { Length: > 0 } stem ? stem : parts[0];
        for (int keep = Math.Min(2, parts.Length - 2); keep >= 1; keep--)
            yield return first + "…​" + string.Join('​', parts[^keep..]);
    }

    static void PlaceAt(Control block, Size size, Point center)
    {
        Canvas.SetLeft(block, center.X - size.Width / 2);
        Canvas.SetTop(block, center.Y - size.Height / 2);
    }

    static double LongestWordWidth(TextBlock text)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight);
        return (text.Text ?? "").Split([' ', '​'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new FormattedText(word, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, text.FontSize, Brushes.Black).WidthIncludingTrailingWhitespace)
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>
    /// Like <see cref="RectFits"/> for the whole block, but each row (label, hint) is checked with its own width: a
    /// short hint under a long name leaves the bottom corners empty.
    /// </summary>
    /// <param name="slack">How much closer to the edges the rows may come, in points.</param>
    static bool RowsFit(StackPanel block, double mid, Size size, double labelR, double innerR, double outerR, double span, double slack)
    {
        var center = OnCircle(labelR, mid);
        double top = center.Y - size.Height / 2;
        foreach (var row in block.Children)
        {
            if (!row.IsVisible)
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

    // ---- center ----

    /// <summary>
    /// Folder names are often one long word (MyCompanyProject): shrink such a name onto one line rather than
    /// breaking it in the middle. Names with spaces wrap onto two lines as usual.
    /// </summary>
    void SetCenterTitle(string title)
    {
        _centerTitle.Text = BreakableName(title);
        foreach (double size in new[] { 12.5, 12, 11.5, 11, 10.5 })
        {
            _centerTitle.FontSize = size;
            if (LongestWordWidth(_centerTitle) > _center.Width)
                continue;
            var text = new FormattedText(_centerTitle.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(_centerTitle.FontFamily, FontStyle.Normal, FontWeight.SemiBold), size, Brushes.Black)
            { MaxTextWidth = _center.Width };
            if (text.Height <= _centerTitle.MaxHeight)
                return;
        }
        _centerTitle.FontSize = 12.5; // too long anyway: wrap and trim
    }

    static string CenterSymbolFor(ContextKind kind) => kind switch
    {
        ContextKind.DesktopEmpty => SymbolIcons.NameFor(''),
        ContextKind.FileItem => SymbolIcons.NameFor(''),
        _ => SymbolIcons.NameFor(''),
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
                if ((char.IsLower(previous) && char.IsUpper(c)) || (char.IsLetter(previous) && char.IsDigit(c)) || previous is '-' or '_' or '.')
                    builder.Append('​');
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    // ---- painting ----

    void Paint(Tile tile, bool hot, bool expanded)
    {
        bool lit = hot || expanded;
        var style = _palette.Theme.Highlight;
        switch (style)
        {
            case HighlightStyle.Rim:
                tile.Fill.Fill = hot ? tile.Wash : _palette.TileFill;
                tile.Fill.Stroke = _palette.TileStroke;
                tile.Rim.Fill = lit ? tile.Lit : _palette.RimIdle;
                tile.Rim.Effect = lit ? _palette.Glow : null;
                break;
            case HighlightStyle.Fill or HighlightStyle.Outline:
                // The whole tile answers: filled with the accent (or brightened), its outline lit.
                tile.Fill.Fill = hot ? tile.Wash : _palette.TileFill;
                tile.Fill.Stroke = lit ? tile.Lit : _palette.TileStroke;
                tile.Fill.StrokeThickness = lit ? 1.5 : 1;
                tile.Fill.Effect = (style == HighlightStyle.Fill ? hot : lit) ? _palette.Glow : null;
                tile.Rim.Fill = null;
                break;
            case HighlightStyle.Glow:
                tile.Fill.Fill = hot ? tile.Wash : expanded ? tile.WashSoft : _palette.TileFill;
                tile.Fill.Stroke = _palette.TileStroke;
                tile.Rim.Fill = null;
                break;
        }
        bool onAccent = hot && style == HighlightStyle.Fill;
        if (tile.Glyph is not null)
            tile.Glyph.Fill = onAccent ? MenuPalette.OnAccent : hot ? _palette.Accent : _palette.Text;
        tile.Label.Foreground = onAccent ? MenuPalette.OnAccent : _palette.Text;
        tile.Hint.Foreground = onAccent ? MenuPalette.OnAccentSoft : hot ? _palette.Accent : _palette.SubText;
        tile.Content.Opacity = tile.Dimmed && !hot ? 0.45 : 1;
        if (tile.Arrow is not null)
        {
            tile.Arrow.Fill = tile.Lit;
            tile.Arrow.Stroke = tile.Lit;
            tile.Arrow.Opacity = lit ? 1 : 0.5;
            tile.Arrow.Effect = lit ? _palette.Glow : null;
        }
    }

    void ApplyPalette()
    {
        _ringBase.Fill = _palette.RingFill;
        _ringBase.Stroke = _palette.RingStroke;
        _ringShadow.Opacity = _palette.ShadowOpacity;
        foreach (var band in _bands.Skip(1))
        {
            if (band.Layer.Effect is DropShadowEffect shadow)
                shadow.Opacity = _palette.ShadowOpacity * 0.8;
        }
        _hub.Fill = _palette.HubFill;
        _hubRing.Stroke = _palette.HubStroke;
        _hubRing.StrokeThickness = _palette.Theme.HubStrokeWidth;
        _hubRing.Effect = _palette.Theme.HubGlow ? _palette.Glow : null;
        _centerGlyph.Fill = _palette.Brand;
        _centerTitle.Foreground = _palette.Text;
        _centerSubtitle.Foreground = _palette.SubText;
    }

    /// <summary>An SF Symbol as a mask over <paramref name="fill"/>.</summary>
    static void SetSymbol(Rectangle target, string symbol, double pointSize, IBrush fill)
    {
        var mask = SymbolIcons.Mask(symbol, pointSize);
        target.OpacityMask = mask is null ? null : new ImageBrush(mask) { Stretch = Stretch.Uniform };
        target.Fill = fill;
    }

    // ---- animation ----

    /// <summary>
    /// The menu and its arcs fade and grow in unless Reduce Motion is on or the Mac is in Low Power Mode: there an
    /// instant menu feels faster than an animation.
    /// </summary>
    static bool Animate => !MenuPanel.ReduceMotion;

    /// <summary>Fades <paramref name="target"/> in while <paramref name="scale"/> grows from <paramref name="from"/> to 1 (cubic ease-out).</summary>
    void FadeIn(Control target, ScaleTransform scale, int milliseconds, double from)
    {
        if (!Animate)
        {
            target.Opacity = 1;
            scale.ScaleX = scale.ScaleY = 1;
            return;
        }
        target.Opacity = 0;
        scale.ScaleX = scale.ScaleY = from;
        var start = DateTime.UtcNow;
        int version = _version;
        DispatcherTimer.Run(() =>
        {
            if (_version != version)
                return false;
            double t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / milliseconds);
            double eased = 1 - Math.Pow(1 - t, 3);
            target.Opacity = eased;
            scale.ScaleX = scale.ScaleY = from + (1 - from) * eased;
            return t < 1;
        }, TimeSpan.FromMilliseconds(8));
    }

    static double Clamp(double value, double min, double max) => min > max ? (min + max) / 2 : Math.Clamp(value, min, max);

    // ---- geometry ----

    static Point OnCircle(double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(StageR + radius * Math.Sin(rad), StageR - radius * Math.Cos(rad));
    }

    /// <summary>
    /// Ring sector between two radii, angles in degrees clockwise from the top. Its side edges are pulled in by half
    /// of <paramref name="gap"/>, so neighbouring tiles have parallel gaps.
    /// </summary>
    static Geometry TileGeometry(double innerR, double outerR, double fromDegrees, double toDegrees, double gap)
    {
        var center = new Point(StageR, StageR);
        if (toDegrees - fromDegrees >= 358)
        {
            return new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Rect(center.X - outerR, center.Y - outerR, 2 * outerR, 2 * outerR)),
                new EllipseGeometry(new Rect(center.X - innerR, center.Y - innerR, 2 * innerR, 2 * innerR)));
        }
        double outerInset = Math.Asin(gap / 2 / outerR) * 180 / Math.PI;
        double innerInset = Math.Asin(gap / 2 / innerR) * 180 / Math.PI;
        double outerFrom = fromDegrees + outerInset, outerTo = toDegrees - outerInset;
        double innerFrom = fromDegrees + innerInset, innerTo = toDegrees - innerInset;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(OnCircle(outerR, outerFrom), isFilled: true);
            ctx.ArcTo(OnCircle(outerR, outerTo), new Size(outerR, outerR), 0, outerTo - outerFrom > 180, SweepDirection.Clockwise);
            ctx.LineTo(OnCircle(innerR, innerTo));
            ctx.ArcTo(OnCircle(innerR, innerFrom), new Size(innerR, innerR), 0, innerTo - innerFrom > 180, SweepDirection.CounterClockwise);
            ctx.EndFigure(isClosed: true);
        }
        return geometry;
    }

    /// <summary>
    /// <see cref="TileGeometry"/> with rounded corners (a petal). Each corner is a quadratic curve from
    /// <paramref name="corner"/> before it to <paramref name="corner"/> after it, smaller when an edge is short.
    /// </summary>
    static Geometry RoundedTileGeometry(double innerR, double outerR, double fromDegrees, double toDegrees, double gap, double corner)
    {
        const double Deg = 180 / Math.PI;
        double outerFrom = fromDegrees + Math.Asin(gap / 2 / outerR) * Deg, outerTo = toDegrees - Math.Asin(gap / 2 / outerR) * Deg;
        double innerFrom = fromDegrees + Math.Asin(gap / 2 / innerR) * Deg, innerTo = toDegrees - Math.Asin(gap / 2 / innerR) * Deg;
        Point a = OnCircle(outerR, outerFrom), b = OnCircle(outerR, outerTo), c = OnCircle(innerR, innerTo), d = OnCircle(innerR, innerFrom);
        double side = Length(b - c);
        double r = Math.Min(corner, Math.Min(side, Math.Min((outerTo - outerFrom) / Deg * outerR, (innerTo - innerFrom) / Deg * innerR)) / 3);
        double outerStep = r / outerR * Deg, innerStep = r / innerR * Deg;
        static double Length(Vector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
        static Point Toward(Point from, Point to, double distance) => from + (to - from) * (distance / Length(to - from));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(OnCircle(outerR, outerFrom + outerStep), isFilled: true);
            ctx.ArcTo(OnCircle(outerR, outerTo - outerStep), new Size(outerR, outerR), 0, outerTo - outerFrom - 2 * outerStep > 180, SweepDirection.Clockwise);
            ctx.QuadraticBezierTo(b, Toward(b, c, r));
            ctx.LineTo(Toward(c, b, r));
            ctx.QuadraticBezierTo(c, OnCircle(innerR, innerTo - innerStep));
            ctx.ArcTo(OnCircle(innerR, innerFrom + innerStep), new Size(innerR, innerR), 0, innerTo - innerFrom - 2 * innerStep > 180,
                SweepDirection.CounterClockwise);
            ctx.QuadraticBezierTo(d, Toward(d, a, r));
            ctx.LineTo(Toward(a, d, r));
            ctx.QuadraticBezierTo(a, OnCircle(outerR, outerFrom + outerStep));
            ctx.EndFigure(isClosed: true);
        }
        return geometry;
    }

    /// <summary>A short band just outside <paramref name="baseR"/> whose outer edge comes to a point: "this tile opens outward".</summary>
    static Geometry ArrowGeometry(double mid, double halfWidth, double baseR)
    {
        double inner = baseR + 5, outer = baseR + 8, tip = baseR + 15;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(OnCircle(inner, mid - halfWidth), isFilled: true);
            ctx.ArcTo(OnCircle(inner, mid + halfWidth), new Size(inner, inner), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(OnCircle(outer, mid + halfWidth));
            ctx.LineTo(OnCircle(tip, mid));
            ctx.LineTo(OnCircle(outer, mid - halfWidth));
            ctx.EndFigure(isClosed: true);
        }
        return geometry;
    }
}
