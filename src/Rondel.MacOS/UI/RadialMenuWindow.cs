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
using Rondel.Core.Localization;
using Rondel.Core.Logging;
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
/// It is a non-activating panel (<see cref="MenuPanel"/>): it takes the keyboard while Finder stays the active app.
/// Fully transparent pixels still catch the pointer on macOS (the window draws with Metal), so a press on the empty
/// stage closes the menu, as a click outside it does.
/// </para>
/// <para>
/// This is the ring. The arcs that slices open (Resume's sessions, Recent Projects) and the input cards come next.
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
    const double ArrowReach = 17;      // how far beyond the ring an arrow (and its hit area) reaches
    const double MinLabelWidth = 56;
    const double ShadowPad = 32;       // shadow extent kept on screen when clamping to the work area
    static readonly double[] RingFontSizes = [12];

    /// <summary>One drawn tile: a slice of the ring.</summary>
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

        public required IBrush Lit { get; init; }
        public bool Dimmed { get; set; }
    }

    readonly Panel _surface = new() { RenderTransformOrigin = RelativePoint.Center };
    readonly ScaleTransform _surfaceScale = new(1, 1);
    readonly Canvas _stage = new() { Width = 2 * StageR, Height = 2 * StageR, Background = Brushes.Transparent };
    readonly Ellipse _ringBase = new() { Width = 2 * OuterR, Height = 2 * OuterR, StrokeThickness = 1 };
    readonly DropShadowEffect _ringShadow = new() { BlurRadius = 30, OffsetY = 6, Color = Colors.Black, Opacity = 0.4 };
    readonly Canvas _sliceLayer = new() { Width = 2 * StageR, Height = 2 * StageR, IsHitTestVisible = false };
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
    readonly List<Tile> _slices = [];

    /// <summary>Label layouts already worked out while this menu is open: fitting a label takes many measurements.</summary>
    readonly Dictionary<(string Label, string Hint, double Mid, double Span), (double FontSize, Size Size)> _labelLayouts = [];

    MenuPalette _palette;
    MenuModel? _model;
    nint _nsWindow;
    bool _nonActivating;
    Path? _orbit;
    double _span = 360, _offset;
    int _hover = -1;
    bool _isOpen, _pressedInside;
    int _version;
    CGPoint _centerOnScreen; // the ring center in screen points

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
        _stage.Children.Add(_sliceLayer);
        _stage.Children.Add(_hubRing);
        _stage.Children.Add(_hub);
        // The center's content is centered on the hub whatever its height (a title on one or two lines).
        var hubContent = new Panel { Width = 2 * HubR, Height = 2 * HubR, IsHitTestVisible = false, Children = { _center } };
        _center.HorizontalAlignment = HorizontalAlignment.Center;
        Canvas.SetLeft(hubContent, StageR - HubR);
        Canvas.SetTop(hubContent, StageR - HubR);
        _stage.Children.Add(hubContent);
        _surface.RenderTransform = _surfaceScale;
        _surface.Children.Add(new Canvas
        {
            Width = 2 * StageR, Height = 2 * StageR, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Children = { _stage }
        });
        Content = _surface;

        ApplyPalette();
        PointerMoved += (_, e) => OnPointer(e.GetPosition(_stage), pressed: false, released: false);
        PointerExited += (_, _) => { if (_isOpen) SetHover(-1); };
        PointerPressed += (_, e) => OnPointer(e.GetPosition(_stage), pressed: true, released: false);
        PointerReleased += (_, e) => OnPointer(e.GetPosition(_stage), pressed: false, released: true);
        KeyDown += OnKeyDown;
        // Losing the key window (another Space, ⌘Tab, a click in another app) closes the menu, as a click outside it does.
        Deactivated += (_, _) => CloseMenu(restoreFocus: false);
    }

    /// <summary>A slice was chosen (click, release over it, number key, Enter).</summary>
    public event Action<MenuModel, MenuItemModel>? ItemInvoked;

    /// <summary>The menu closed; the frame it had (screen points, top-left origin) is no longer Rondel's.</summary>
    public event Action? MenuClosed;

    public bool IsMenuOpen => _isOpen;

    /// <summary>
    /// Creates the native window and draws a menu once, invisibly and on screen (off screen the window came back
    /// 0×0), so the first real open is instant.
    /// </summary>
    public void Prepare()
    {
        var agent = BuiltInAgents.ClaudeCode();
        var warmup = AppSettings.DefaultActions().Take(4).Select(a => new MenuItemModel(a, agent, a.Id, a.Glyph, "warm-up", true)).ToList();
        _model = new MenuModel(TriggerContext.None("warm-up"), "warm-up", "warm-up", warmup);
        Width = Height = StageSize;
        BuildSlices(_model);
        SetHover(0);
        SetHover(-1);
        Position = new PixelPoint(0, 0);
        Opacity = 0;
        Show();
        _nsWindow = TryGetPlatformHandle()?.Handle ?? 0;
        _nonActivating = MenuPanel.Configure(_nsWindow);
        Hide();
        Opacity = 1;
    }

    /// <summary>Opens the menu centered on <paramref name="anchor"/>, kept inside the work area of its screen.</summary>
    /// <returns>The window's frame in screen points, for the event tap.</returns>
    public CGRect ShowMenu(MenuModel model, CGPoint anchor)
    {
        _version++;
        _model = model;
        _pressedInside = false;
        _labelLayouts.Clear();
        BuildSlices(model);
        SetCenterTitle(model.Title);
        _centerSubtitle.Text = model.Subtitle ?? "";
        _centerSubtitle.IsVisible = !string.IsNullOrEmpty(model.Subtitle);
        SetSymbol(_centerGlyph, CenterSymbolFor(model.Context.Kind), 18, _palette.Brand);

        // Center on the pointer, but keep the ring and its shadow inside the screen's work area.
        var screen = Screens.ScreenFromPoint(new PixelPoint((int)anchor.X, (int)anchor.Y)) ?? Screens.Primary;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 10_000, 10_000);
        double half = OuterR + ShadowPad;
        double cx = Clamp(anchor.X, work.X + half, work.Right - half), cy = Clamp(anchor.Y, work.Y + half, work.Bottom - half);
        _centerOnScreen = new CGPoint(cx, cy);
        double size = StageSize;
        Width = Height = size;
        Position = new PixelPoint((int)Math.Round(cx - size / 2), (int)Math.Round(cy - size / 2));

        _surface.Opacity = 0;
        _surfaceScale.ScaleX = _surfaceScale.ScaleY = Animate ? 0.86 : 1;
        MenuPanel.BeforeShow(_nsWindow, _nonActivating);
        ShowActivated = !_nonActivating;
        Show();
        MenuPanel.TakeKey(_nsWindow, _nonActivating);
        _isOpen = true;
        AnimateOpen();
        return new CGRect { X = Position.X, Y = Position.Y, Width = size, Height = size };
    }

    /// <summary>The pointer moved while the button that opened the menu is still down (from the event tap).</summary>
    public void OnHeldMove(CGPoint point)
    {
        if (_isOpen)
            SetHover(HitTest(point.X - _centerOnScreen.X, point.Y - _centerOnScreen.Y));
    }

    /// <summary>The button that opened the menu was released at <paramref name="point"/> (from the event tap).</summary>
    public void OnHeldRelease(CGPoint point)
    {
        if (!_isOpen)
            return;
        int index = HitTest(point.X - _centerOnScreen.X, point.Y - _centerOnScreen.Y);
        if (index >= 0)
            Invoke(index);
    }

    public void CloseMenu(bool restoreFocus)
    {
        if (!_isOpen)
            return;
        _isOpen = false;
        int version = ++_version;
        SetHover(-1);
        MenuClosed?.Invoke();
        MenuPanel.AfterClose(_nonActivating, restoreFocus);
        _surface.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (_version == version)
                Hide();
        }, TimeSpan.FromMilliseconds(Animate ? 110 : 0));
    }

    /// <summary>Switches to another theme or mode; an open menu is redrawn with what it shows.</summary>
    public void ApplyLook(MenuPalette palette)
    {
        _palette = palette;
        ApplyPalette();
        if (!_isOpen || _model is null)
            return;
        var hints = _slices.Select(s => (s.Hint.IsVisible ? s.Hint.Text : null, s.Dimmed)).ToList();
        BuildSlices(_model);
        ApplyHints(_model, hints);
    }

    /// <summary>
    /// Late session hints (looked up off the main thread after the menu opened). Ignored if the menu has moved on
    /// to another context meanwhile.
    /// </summary>
    public void ApplyHints(MenuModel model, IReadOnlyList<(string? Hint, bool Dimmed)> hints)
    {
        if (!ReferenceEquals(model, _model) || hints.Count != _slices.Count)
            return;
        for (int i = 0; i < hints.Count; i++)
        {
            var slice = _slices[i];
            if (hints[i].Hint is not { } text)
                continue;
            slice.Hint.Text = text;
            slice.Hint.IsVisible = true;
            slice.Hint.Opacity = 1;
            slice.Dimmed = hints[i].Dimmed;
            Paint(slice, i == _hover);
        }
    }

    /// <summary>
    /// Developer aid (--render-menu): draws the menu for <paramref name="model"/> into a bitmap at 2x, without showing
    /// the window, on a solid background so the shadows read as they would on screen.
    /// </summary>
    public Bitmap RenderPreview(MenuModel model, IReadOnlyList<(string? Hint, bool Dimmed)> hints, int hover, Color background)
    {
        _model = model;
        _labelLayouts.Clear();
        BuildSlices(model);
        SetCenterTitle(model.Title);
        _centerSubtitle.Text = model.Subtitle ?? "";
        _centerSubtitle.IsVisible = !string.IsNullOrEmpty(model.Subtitle);
        SetSymbol(_centerGlyph, CenterSymbolFor(model.Context.Kind), 18, _palette.Brand);
        ApplyHints(model, hints);
        SetHover(hover);
        _surface.Opacity = 1;
        double size = StageSize;
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

    // ---- input ----

    void OnPointer(Point stagePoint, bool pressed, bool released)
    {
        if (!_isOpen)
            return;
        double dx = stagePoint.X - StageR, dy = stagePoint.Y - StageR;
        int index = HitTest(dx, dy);
        if (pressed)
        {
            // Transparent pixels catch the pointer on macOS: a press on the empty stage is a click outside the menu.
            if (index < 0 && Math.Sqrt(dx * dx + dy * dy) > OuterR + ArrowReach)
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
            if (wasPressed && index >= 0)
                Invoke(index);
            return;
        }
        SetHover(index);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isOpen || _slices.Count == 0)
            return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        int number = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
            _ => -1,
        };
        int count = _slices.Count;
        e.Handled = true;
        if (number >= 0)
        {
            if (number < count)
                Invoke(number);
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                CloseMenu(restoreFocus: true);
                break;
            case Key.Enter or Key.Space:
                if (_hover >= 0)
                    Invoke(_hover);
                break;
            case Key.Right or Key.Down or Key.Tab when !shift:
                SetHover(_hover < 0 ? 0 : (_hover + 1) % count);
                break;
            case Key.Left or Key.Up or Key.Tab:
                SetHover(_hover < 0 ? count - 1 : (_hover - 1 + count) % count);
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    void Invoke(int index)
    {
        if (_model is null || index < 0 || index >= _slices.Count)
            return;
        var tile = _slices[index];
        SetHover(index);
        // The Recent Projects slice launches nothing itself: it opens into its projects (with the arcs).
        if (tile.Item.Action.Source == ActionSource.RecentProjects && tile.Item.Directory is null)
            return;
        ItemInvoked?.Invoke(_model, tile.Item);
    }

    /// <summary>The slice under an offset from the ring center (points), or -1.</summary>
    int HitTest(double dx, double dy)
    {
        if (_slices.Count == 0)
            return -1;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < DeadZoneR || distance > OuterR + 1)
            return -1;
        double angle = Math.Atan2(dx, -dy) * 180 / Math.PI; // 0° = up, clockwise
        double relative = ((angle - _offset + _span / 2) % 360 + 360) % 360;
        return Math.Min((int)(relative / _span), _slices.Count - 1);
    }

    void SetHover(int index)
    {
        if (index == _hover)
            return;
        int previous = _hover;
        _hover = index;
        if (previous >= 0 && previous < _slices.Count)
            Paint(_slices[previous], hot: false);
        if (index >= 0 && index < _slices.Count)
            Paint(_slices[index], hot: true);
    }

    // ---- slices ----

    /// <summary>The window holds the ring, its arrows and its shadow (the arcs will need more).</summary>
    static double StageSize => Math.Ceiling(2 * (OuterR + ArrowReach + ShadowPad));

    void BuildSlices(MenuModel model)
    {
        _sliceLayer.Children.Clear();
        _slices.Clear();
        _hover = -1;
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
            var tile = CreateTile(item, mid, _span);
            bool expectsHint = item.Action.Hint != SessionHint.None;
            tile.Hint.Text = item.Hint ?? (expectsHint ? hintSample : "");
            tile.Hint.IsVisible = item.Hint is not null || expectsHint;
            tile.Hint.Opacity = item.Hint is not null ? 1 : 0;
            tile.Dimmed = item.Dimmed;
            PlaceLabel(tile, mid, maxLabelWidth);
            _slices.Add(tile);
            Paint(tile, hot: false);
        }
        _orbit = _palette.Theme.Decoration == RingDecoration.Orbit ? OrbitDecoration(n) : null;
        if (_orbit is not null)
            _sliceLayer.Children.Add(_orbit);
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

    Tile CreateTile(MenuItemModel item, double mid, double span)
    {
        var theme = _palette.Theme;
        double from = mid - span / 2, to = mid + span / 2;
        double gap = span < 360 ? theme.TileGap : 0;
        var shape = theme.Tiles == TileShape.Rounded && span < 360
            ? RoundedTileGeometry(TileInnerR, TileOuterR, from, to, gap, theme.CornerRadius)
            : TileGeometry(TileInnerR, TileOuterR, from, to, gap);
        var fill = new Path { Data = shape, StrokeThickness = 1 };
        var rim = new Path { Data = TileGeometry(TileOuterR - theme.RimWidth, TileOuterR, from, to, gap) };
        _sliceLayer.Children.Add(fill);
        _sliceLayer.Children.Add(rim);

        var glyph = new Rectangle { Width = 22, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        SetSymbol(glyph, SymbolIcons.NameFor(item.Glyph), 20, _palette.Text);
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
        var stack = new StackPanel { Children = { glyph, label, hint } };
        _sliceLayer.Children.Add(stack);

        // The rim's gradient runs along the arc; the wash deepens towards the outer edge.
        double half = Math.Min(span, 359) / 2;
        var (inner, outer) = (OnCircle(TileInnerR, mid), OnCircle(TileOuterR, mid));
        var center = OnCircle((TileInnerR + TileOuterR) / 2, mid);
        // A glow stays inside its own wedge, so neighbouring glows never touch.
        double glowR = Math.Min(Math.Min(56, (TileOuterR - TileInnerR) * 0.62),
            (TileInnerR + TileOuterR) / 2 * Math.Sin(Math.Min(span, 180) * Math.PI / 360) * 0.95);
        var wash = theme.Highlight switch
        {
            HighlightStyle.Fill => _palette.AccentFillBetween(inner, outer),
            HighlightStyle.Outline => _palette.HotFill,
            HighlightStyle.Glow => _palette.GlowAround(center, glowR, 1),
            _ => _palette.WashBetween(inner, outer),
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
            Wash = wash,
            Lit = _palette.BrandBetween(OnCircle(TileOuterR, mid - half), OnCircle(TileOuterR, mid + half)),
        };
    }

    /// <summary>
    /// Centers a tile's icon + label on its middle angle, using the widest width (≤ max) at which the measured block
    /// stays inside its band and its own wedge. Narrower widths wrap the label onto a second line, which matters for
    /// the left and right slices where the ring is only about 100 points deep.
    /// </summary>
    void PlaceLabel(Tile tile, double mid, double maxWidth)
    {
        var (block, label) = (tile.Content, tile.Label);
        var key = (label.Text ?? "", tile.Hint.IsVisible ? tile.Hint.Text ?? "" : "", Math.Round(mid, 1), _span);
        if (_labelLayouts.TryGetValue(key, out var known))
        {
            label.FontSize = known.FontSize;
            block.Width = known.Size.Width;
            PlaceAt(block, known.Size, OnCircle(LabelR, mid));
            return;
        }

        Size last = default;
        foreach (double fontSize in RingFontSizes)
        {
            label.FontSize = fontSize;
            // Never narrower than the longest word, so words are not broken in the middle.
            double minWidth = Math.Clamp(LongestWordWidth(label) + 2, MinLabelWidth, maxWidth);
            // Steps of 4 down from the widest width, then one step back up: fitting is not monotonic (a narrower,
            // taller block can fail where a wider one fits), so the steps stay small.
            for (double width = maxWidth; ; width = Math.Max(minWidth, width - 4))
            {
                if (TryWidth(width) is { } fit)
                {
                    for (double wider = Math.Min(maxWidth, width + 2); wider > width; wider -= 2)
                    {
                        if (TryWidth(wider) is { } better)
                        {
                            fit = better;
                            break;
                        }
                    }
                    block.Width = fit.Width;
                    _labelLayouts[key] = (fontSize, fit);
                    PlaceAt(block, fit, OnCircle(LabelR, mid));
                    return;
                }
                if (width <= minWidth)
                    break;
            }
        }
        block.Width = last.Width;
        PlaceAt(block, last, OnCircle(LabelR, mid)); // best effort with the smallest size tried

        Size? TryWidth(double width)
        {
            block.Width = width;
            block.Measure(new Size(width, double.PositiveInfinity));
            var size = new Size(width, block.DesiredSize.Height);
            last = size;
            return RectFits(OnCircle(LabelR, mid), size, mid, TileInnerR, TileOuterR, _span) ? size : null;
        }
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

    static bool RectFits(Point center, Size size, double mid, double innerR, double outerR, double span)
    {
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

    /// <summary>Signed angle from <paramref name="from"/> to <paramref name="to"/>, in (-180, 180].</summary>
    static double AngleBetween(double from, double to) => ((to - from) % 360 + 540) % 360 - 180;

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

    void Paint(Tile tile, bool hot)
    {
        var style = _palette.Theme.Highlight;
        switch (style)
        {
            case HighlightStyle.Rim:
                tile.Fill.Fill = hot ? tile.Wash : _palette.TileFill;
                tile.Fill.Stroke = _palette.TileStroke;
                tile.Rim.Fill = hot ? tile.Lit : _palette.RimIdle;
                tile.Rim.Effect = hot ? _palette.Glow : null;
                break;
            case HighlightStyle.Fill or HighlightStyle.Outline:
                // The whole tile answers: filled with the accent (or brightened), its outline lit.
                tile.Fill.Fill = hot ? tile.Wash : _palette.TileFill;
                tile.Fill.Stroke = hot ? tile.Lit : _palette.TileStroke;
                tile.Fill.StrokeThickness = hot ? 1.5 : 1;
                tile.Fill.Effect = hot ? _palette.Glow : null;
                tile.Rim.Fill = null;
                break;
            case HighlightStyle.Glow:
                tile.Fill.Fill = hot ? tile.Wash : _palette.TileFill;
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
    }

    void ApplyPalette()
    {
        _ringBase.Fill = _palette.RingFill;
        _ringBase.Stroke = _palette.RingStroke;
        _ringShadow.Opacity = _palette.ShadowOpacity;
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

    // ---- open and close ----

    /// <summary>
    /// The menu fades and grows in unless Reduce Motion is on or the Mac is in Low Power Mode: there an instant menu
    /// feels faster than an animation.
    /// </summary>
    static bool Animate => !MenuPanel.ReduceMotion;

    void AnimateOpen()
    {
        if (!Animate)
        {
            _surface.Opacity = 1;
            return;
        }
        var start = DateTime.UtcNow;
        int version = _version;
        DispatcherTimer.Run(() =>
        {
            if (_version != version)
                return false;
            double t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / 160);
            double eased = 1 - Math.Pow(1 - t, 3);
            _surface.Opacity = eased;
            _surfaceScale.ScaleX = _surfaceScale.ScaleY = 0.86 + 0.14 * eased;
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
}
