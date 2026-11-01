using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Rondel.Core.Flow;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Settings;

namespace Rondel.MacOS.UI;

/// <summary>
/// The card the menu turns into when an action needs more than a click, the counterpart of the Windows input card:
/// a folder name, then a prompt (<see cref="InputSpec"/>), or the New Project card with the name, how the project
/// starts (prompt, git, plan mode), the templates copied into it and the prompt, all on one screen
/// (<see cref="NewProjectSpec"/>). It opens with the choice made last time, so the usual project is a name and Return.
/// Keys follow the Windows card, with ⌘↩ (or ⌃↩) where Windows uses Ctrl+Enter.
/// </summary>
internal sealed class InputCard : Border
{
    /// <summary>The script carries the prompt in a file, so there is no command-line limit; this only stops a paste gone wrong.</summary>
    const int MaxPromptLength = 100_000;

    readonly Rectangle _glyph = new() { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _title = new() { FontSize = 13.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 290 };
    readonly TextBlock _location = new() { FontSize = 11, Margin = new Thickness(0, 3, 0, 10), TextTrimming = TextTrimming.PathSegmentEllipsis };
    readonly TextBox _name = Box();
    readonly StackPanel _options = new() { Margin = new Thickness(0, 12, 0, 0), IsVisible = false };
    readonly TextBlock _startLabel = Caption(new Thickness(1, 0, 0, 6));
    readonly WrapPanel _startChips = new();
    readonly DockPanel _templatesHeader = new() { Margin = new Thickness(1, 6, 0, 6) };
    readonly TextBlock _templatesLabel = Caption(default);
    readonly TextBlock _openTemplates = new() { FontSize = 11, Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Right };
    readonly WrapPanel _templateChips = new();
    readonly TextBlock _noTemplates = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(1, 0, 0, 6) };
    readonly TextBlock _promptLabel = Caption(new Thickness(1, 6, 0, 6));
    readonly TextBox _prompt = Box();
    readonly TextBlock _message = new() { FontSize = 11.5, Margin = new Thickness(1, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly List<Chip> _templateChipList = [];

    MenuPalette _palette;
    Action<bool>? _close;
    InputSpec? _input;
    NewProjectSpec? _newProject;
    Chip? _promptChip, _gitChip, _planChip;
    bool _promptStage;
    string _acceptedName = "";

    public InputCard(MenuPalette palette)
    {
        _palette = palette;
        Width = 360;
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1.2);
        Padding = new Thickness(16, 14, 16, 12);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;
        Effect = new DropShadowEffect { BlurRadius = 30, OffsetY = 6, Color = Colors.Black, Opacity = 0.4 };

        _prompt.AcceptsReturn = true;
        _prompt.TextWrapping = TextWrapping.Wrap;
        _prompt.Height = 132;
        _prompt.VerticalContentAlignment = VerticalAlignment.Top;
        // A single line scrolls sideways without bars; the prompt wraps and shows a bar only when it is long.
        ScrollViewer.SetHorizontalScrollBarVisibility(_name, Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden);
        ScrollViewer.SetVerticalScrollBarVisibility(_name, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_prompt, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_prompt, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        DockPanel.SetDock(_openTemplates, Dock.Right);
        _templatesHeader.Children.Add(_openTemplates);
        _templatesHeader.Children.Add(_templatesLabel);
        _options.Children.Add(_startLabel);
        _options.Children.Add(_startChips);
        _options.Children.Add(_templatesHeader);
        _options.Children.Add(_templateChips);
        _options.Children.Add(_noTemplates);
        Child = new StackPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { _glyph, _title } },
                _location, _name, _options, _promptLabel, _prompt, _message,
            },
        };

        _name.TextChanged += (_, _) =>
        {
            if (_input is not null && !_promptStage)
                UpdateNameFeedback();
        };
        _name.GotFocus += (_, _) => UpdateNewProjectHint();
        _prompt.GotFocus += (_, _) => UpdateNewProjectHint();
        _openTemplates.PointerReleased += (_, _) => OpenTemplates();
        // Before the text boxes: Return, Tab and Esc belong to the card.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ApplyPalette(palette);
    }

    public bool IsActive => _input is not null;

    /// <summary>Opens the card for a folder name (and a prompt), or a prompt alone.</summary>
    /// <param name="close">Closes the menu; true gives the focus back to the app that had it.</param>
    public void Start(InputSpec spec, Action<bool> close)
    {
        Reset();
        _input = spec;
        _close = close;
        SetSymbol(spec.Glyph);
        _name.PlaceholderText = Loc.Get("Input.NamePlaceholder");
        _prompt.PlaceholderText = Loc.Format("Input.PromptPlaceholder", spec.AgentName);
        IsVisible = true;
        if (spec.AskName)
            ShowNameStage();
        else
            ShowPromptStage(name: null);
    }

    /// <summary>Opens the New Project card.</summary>
    public void Start(NewProjectSpec spec, Action<bool> close)
    {
        Start(new InputSpec(spec.Title, spec.Glyph, spec.ParentDir, spec.AgentName, AskPrompt: false, spec.ValidateName, Submit: (_, _) => null), close);
        _newProject = spec;
        _startLabel.Text = Loc.Get("NewProject.Start");
        _promptChip = spec.CanPrompt ? AddChip(_startChips, Loc.Get("NewProject.Prompt"), spec.Initial.Prompt) : null;
        _gitChip = spec.CanGit ? AddChip(_startChips, Loc.Get("NewProject.Git"), spec.Initial.Git) : null;
        _planChip = spec.CanPlan ? AddChip(_startChips, Loc.Get("NewProject.Plan"), spec.Initial.Plan) : null;
        if (_promptChip is not null)
            _promptChip.Changed += UpdateNewProjectLayout;

        // Options turned off in Settings are not offered; their sections go when nothing is left in them.
        bool anyStart = _startChips.Children.Count > 0;
        _startLabel.IsVisible = _startChips.IsVisible = anyStart;
        _templatesHeader.IsVisible = spec.OffersTemplates;
        _templatesHeader.Margin = new Thickness(1, anyStart ? 6 : 0, 0, 6);
        _options.IsVisible = anyStart || spec.OffersTemplates;
        _templatesLabel.Text = Loc.Get("NewProject.Templates");
        _openTemplates.Text = Loc.Get("NewProject.OpenTemplates");
        _openTemplates.IsVisible = spec.TemplatesDirectory is not null;
        foreach (string name in spec.Templates)
            _templateChipList.Add(AddChip(_templateChips, name, spec.Initial.Templates.Contains(name, StringComparer.OrdinalIgnoreCase)));
        _templateChips.IsVisible = spec.Templates.Count > 0;
        _noTemplates.Text = Loc.Get("NewProject.NoTemplates");
        _noTemplates.IsVisible = spec.OffersTemplates && spec.Templates.Count == 0;
        _promptLabel.Text = Loc.Get("NewProject.PromptSection");
        UpdateNewProjectLayout();
    }

    /// <summary>Developer aid (--render-new): types a name and a prompt as if the user had.</summary>
    public void Fill(string name, string? prompt)
    {
        _name.Text = name;
        _prompt.Text = prompt ?? "";
        UpdateNewProjectLayout();
    }

    /// <summary>Back to hidden, ready for the next card.</summary>
    public void Reset()
    {
        _input = null;
        _newProject = null;
        _promptChip = _gitChip = _planChip = null;
        _promptStage = false;
        _startChips.Children.Clear();
        _templateChips.Children.Clear();
        _templateChipList.Clear();
        _options.IsVisible = false;
        _promptLabel.IsVisible = false;
        _name.Text = "";
        _prompt.Text = "";
        IsVisible = false;
    }

    public void ApplyPalette(MenuPalette palette)
    {
        _palette = palette;
        Background = palette.Card;
        BorderBrush = palette.Brand;
        _glyph.Fill = palette.Brand;
        _title.Foreground = palette.Text;
        _location.Foreground = palette.SubText;
        // The theme recolors a text box on hover and focus from these resources; they all take the menu's palette.
        foreach (string state in new[] { "", "PointerOver", "Focused" })
        {
            Resources["TextControlBackground" + state] = palette.InputBackground;
            Resources["TextControlBorderBrush" + state] = palette.Accent;
            Resources["TextControlForeground" + state] = palette.Text;
            Resources["TextControlPlaceholderForeground" + state] = palette.SubText;
        }
        Resources["TextControlSelectionHighlightColor"] = palette.Accent;
        foreach (var box in new[] { _name, _prompt })
        {
            box.Background = palette.InputBackground;
            box.BorderBrush = palette.Accent;
            box.Foreground = palette.Text;
            box.CaretBrush = palette.Text;
            box.SelectionBrush = palette.Accent;
            box.PlaceholderForeground = palette.SubText;
        }
        foreach (var label in new[] { _startLabel, _promptLabel, _templatesLabel, _noTemplates })
            label.Foreground = palette.SubText;
        _openTemplates.Foreground = palette.Accent;
        foreach (var chip in _startChips.Children.Concat(_templateChips.Children).OfType<Chip>())
            chip.ApplyPalette(palette);
    }

    // ---- plain card: a name, then a prompt ----

    void ShowNameStage()
    {
        _promptStage = false;
        _title.Text = _input!.Title;
        _prompt.IsVisible = false;
        _name.IsVisible = true;
        UpdateNameFeedback();
        _name.Focus();
        _name.SelectAll();
    }

    /// <param name="name">The accepted folder name, or null when only a prompt is asked for.</param>
    void ShowPromptStage(string? name)
    {
        var input = _input!;
        _promptStage = true;
        _acceptedName = name ?? "";
        _title.Text = name is null ? input.Title : $"{input.Title} · {name}";
        _location.Text = name is null ? input.Location ?? input.ParentDir : System.IO.Path.Combine(input.ParentDir, name);
        _name.IsVisible = false;
        _prompt.IsVisible = true;
        SetMessage(Loc.Get(input.AskName ? "Input.HintPrompt.Mac" : "Input.HintPromptOnly.Mac"), isError: false);
        _prompt.Focus();
        _prompt.CaretIndex = _prompt.Text?.Length ?? 0;
    }

    void UpdateNameFeedback()
    {
        var input = _input!;
        string name = _name.Text ?? "";
        _location.Text = name.Length == 0 ? input.ParentDir : System.IO.Path.Combine(input.ParentDir, name);
        string? error = name.Length == 0 ? null : input.ValidateName(name);
        string hint = _newProject is not null
            ? Loc.Get(PromptOn ? "Input.HintNewNext" : "Input.HintNew")
            : Loc.Get(input.AskPrompt ? "Input.HintNameNext" : "Input.HintName");
        SetMessage(error ?? hint, error is not null);
    }

    void SetMessage(string text, bool isError)
    {
        _message.Text = text;
        _message.Foreground = isError ? _palette.Error : _palette.SubText;
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_input is null)
            return;
        if (_newProject is not null)
        {
            OnNewProjectKeyDown(e);
            return;
        }
        var input = _input;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_promptStage && input.AskName)
                ShowNameStage();
            else
                _close?.Invoke(true);
            return;
        }
        if (e.Key is not (Key.Enter or Key.Return))
            return;

        if (!_promptStage)
        {
            e.Handled = true;
            string name = _name.Text ?? "";
            if (input.ValidateName(name) is { } error)
                SetMessage(error, isError: true);
            else if (input.AskPrompt)
                ShowPromptStage(name);
            else
                Submit(name, null);
        }
        else if (SubmitModifier(e))
        {
            // Return adds a line to the prompt; ⌘↩ submits.
            e.Handled = true;
            if (CheckedPrompt() is { } prompt)
                Submit(_acceptedName, prompt);
        }
    }

    void Submit(string name, string? prompt)
    {
        if (_input?.Submit(name, prompt) is { } error)
            SetMessage(error, isError: true);
    }

    /// <returns>The trimmed prompt, or null after showing why it cannot be used.</returns>
    string? CheckedPrompt()
    {
        string prompt = (_prompt.Text ?? "").Trim();
        if (prompt.Length == 0)
        {
            SetMessage(Loc.Get("Input.PromptEmpty"), isError: true);
            _prompt.Focus();
            return null;
        }
        if (prompt.Length > MaxPromptLength)
        {
            SetMessage(Loc.Format("Input.PromptTooLong", prompt.Length, MaxPromptLength), isError: true);
            return null;
        }
        return prompt;
    }

    /// <summary>⌘↩, the Mac's "send" (⌃↩ too, as Ctrl+Enter on Windows).</summary>
    static bool SubmitModifier(KeyEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);

    // ---- New Project ----

    bool PromptOn => _promptChip?.IsOn == true;

    /// <summary>The prompt box is there only while the Prompt option is on.</summary>
    void UpdateNewProjectLayout()
    {
        if (_newProject is null)
            return;
        _promptLabel.IsVisible = PromptOn;
        _prompt.IsVisible = PromptOn;
        if (!PromptOn && _prompt.IsFocused)
            _name.Focus();
        UpdateNewProjectHint();
    }

    void UpdateNewProjectHint()
    {
        if (_newProject is null)
            return;
        if (_prompt.IsFocused)
            SetMessage(Loc.Get("Input.HintNewPrompt.Mac"), isError: false);
        else
            UpdateNameFeedback();
    }

    void OnNewProjectKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                _close?.Invoke(true);
                break;
            case Key.Tab:
                e.Handled = true;
                MoveFocus(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                break;
            case Key.Enter or Key.Return when SubmitModifier(e):
                e.Handled = true;
                SubmitNewProject();
                break;
            case Key.Enter or Key.Return when !_prompt.IsFocused:
                // In the name or on an option: on to the prompt when there is one, otherwise create.
                e.Handled = true;
                if (_newProject!.ValidateName(_name.Text ?? "") is { } error)
                {
                    SetMessage(error, isError: true);
                    _name.Focus();
                }
                else if (PromptOn)
                {
                    _prompt.Focus();
                    _prompt.CaretIndex = _prompt.Text?.Length ?? 0;
                }
                else
                {
                    SubmitNewProject();
                }
                break;
        }
        // Space on an option turns it on or off; Return in the prompt starts a new line.
    }

    /// <summary>Tab order: the name, the start options, the templates, then the prompt.</summary>
    void MoveFocus(int step)
    {
        var order = new List<Control> { _name };
        order.AddRange(_startChips.Children.OfType<Chip>());
        order.AddRange(_templateChipList);
        if (PromptOn)
            order.Add(_prompt);
        int current = order.FindIndex(c => c.IsFocused);
        int next = current < 0 ? 0 : ((current + step) % order.Count + order.Count) % order.Count;
        order[next].Focus(NavigationMethod.Tab);
        UpdateNewProjectHint();
    }

    void SubmitNewProject()
    {
        var spec = _newProject!;
        string name = _name.Text ?? "";
        if (spec.ValidateName(name) is { } error)
        {
            SetMessage(error, isError: true);
            _name.Focus();
            return;
        }
        string? prompt = null;
        if (PromptOn && (prompt = CheckedPrompt()) is null)
            return;
        // An option the agent cannot offer keeps its last value, for the next time another agent can.
        var options = new NewProjectOptions
        {
            Prompt = _promptChip is null ? spec.Initial.Prompt : PromptOn,
            Git = _gitChip is null ? spec.Initial.Git : _gitChip.IsOn,
            Plan = _planChip is null ? spec.Initial.Plan : _planChip.IsOn,
            Templates = [.. spec.Templates.Where((_, i) => _templateChipList[i].IsOn)],
        };
        if (spec.Submit(new NewProjectRequest(name, options, prompt)) is { } failed)
            SetMessage(failed, isError: true);
    }

    void OpenTemplates()
    {
        if (_newProject?.TemplatesDirectory is not { } directory)
            return;
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("/usr/bin/open", [directory]) { UseShellExecute = false })?.Dispose();
            _close?.Invoke(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"Opening the templates folder '{directory}' failed", ex);
            SetMessage(ex.Message, isError: true);
        }
    }

    Chip AddChip(Panel panel, string text, bool on)
    {
        var chip = new Chip(text, on, _palette);
        // A click takes the focus; typing goes on where it was: the name until there is one, then the prompt.
        chip.Clicked += () =>
        {
            if ((_name.Text ?? "").Length > 0 && PromptOn)
                _prompt.Focus();
            else
                _name.Focus();
        };
        panel.Children.Add(chip);
        return chip;
    }

    void SetSymbol(char glyph)
    {
        var mask = SymbolIcons.Mask(SymbolIcons.NameFor(glyph), 16);
        _glyph.OpacityMask = mask is null ? null : new ImageBrush(mask) { Stretch = Stretch.Uniform };
    }

    static TextBox Box() => new()
    {
        FontSize = 13,
        Padding = new Thickness(8, 6),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
    };

    static TextBlock Caption(Thickness margin) => new() { FontSize = 11, Margin = margin };

    /// <summary>An option on the New Project card: a pill that is lit while it is on; Space or a click turns it over.</summary>
    sealed class Chip : Border
    {
        readonly TextBlock _check = new() { Text = "✓", FontSize = 10, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _text = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        MenuPalette _palette;

        public Chip(string text, bool on, MenuPalette palette)
        {
            _palette = palette;
            _text.Text = text;
            IsOn = on;
            Focusable = true;
            Cursor = new Cursor(StandardCursorType.Hand);
            CornerRadius = new CornerRadius(13);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(11, 4, 12, 5);
            Margin = new Thickness(0, 0, 6, 6);
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _check, _text } };
            PointerReleased += (_, _) => Toggle();
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Space)
                {
                    e.Handled = true;
                    IsOn = !IsOn;
                    Paint();
                    Changed?.Invoke();
                }
            };
            GotFocus += (_, _) => Paint();
            LostFocus += (_, _) => Paint();
            Paint();
        }

        public bool IsOn { get; private set; }

        public event Action? Changed;

        public event Action? Clicked;

        void Toggle()
        {
            IsOn = !IsOn;
            Paint();
            Changed?.Invoke();
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
            _check.IsVisible = IsOn;
            Background = IsOn ? new SolidColorBrush(Color.FromArgb(0x2E, _palette.BrandFrom.R, _palette.BrandFrom.G, _palette.BrandFrom.B)) : Brushes.Transparent;
            BorderBrush = IsOn || IsFocused ? _palette.Accent : new SolidColorBrush(Color.FromArgb(0x38, text.R, text.G, text.B));
            BorderThickness = new Thickness(IsFocused ? 1.5 : 1);
            _check.Foreground = _text.Foreground = IsOn ? _palette.Text : _palette.SubText;
        }
    }
}
