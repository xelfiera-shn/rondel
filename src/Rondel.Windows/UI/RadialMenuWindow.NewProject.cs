using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rondel.Core.Flow;
using Rondel.Core.Localization;
using Rondel.Core.Logging;
using Rondel.Core.Settings;

namespace Rondel.Windows.UI;

/// <summary>
/// The New Project card: the input card with the folder name, how the project starts (prompt, git, plan mode),
/// the templates copied into it and the prompt, all on one screen. It opens with the choice made last time,
/// so the usual project is a name and Enter.
/// </summary>
public partial class RadialMenuWindow
{
    NewProjectSpec? _newProject;
    ToggleButton? _promptChip;
    ToggleButton? _gitChip;
    ToggleButton? _planChip;
    readonly List<ToggleButton> _templateChips = [];

    internal void StartNewProject(NewProjectSpec spec)
    {
        if (!_isOpen)
            return;
        SetUpNewProject(spec);
        // Input mode, the name check and the card's animation are the plain input card's.
        StartInput(new InputSpec(spec.Title, spec.Glyph, spec.ParentDir, spec.AgentName, AskPrompt: false, spec.ValidateName, Submit: (_, _) => null));
        UpdateNewProjectLayout();
    }

    void SetUpNewProject(NewProjectSpec spec)
    {
        _newProject = spec;
        StartLabel.Text = Loc.Get("NewProject.Start");
        StartChips.Children.Clear();
        _promptChip = spec.CanPrompt ? AddChip(StartChips, Loc.Get("NewProject.Prompt"), spec.Initial.Prompt) : null;
        _gitChip = spec.CanGit ? AddChip(StartChips, Loc.Get("NewProject.Git"), spec.Initial.Git) : null;
        _planChip = spec.CanPlan ? AddChip(StartChips, Loc.Get("NewProject.Plan"), spec.Initial.Plan) : null;
        if (_promptChip is not null)
        {
            _promptChip.Checked += (_, _) => UpdateNewProjectLayout();
            _promptChip.Unchecked += (_, _) => UpdateNewProjectLayout();
        }

        // Options turned off in Settings are not offered; their sections go when nothing is left in them.
        bool anyStart = StartChips.Children.Count > 0;
        StartLabel.Visibility = anyStart ? Visibility.Visible : Visibility.Collapsed;
        StartChips.Visibility = anyStart ? Visibility.Visible : Visibility.Collapsed;
        TemplatesHeader.Visibility = spec.OffersTemplates ? Visibility.Visible : Visibility.Collapsed;
        TemplatesHeader.Margin = new Thickness(1, anyStart ? 6 : 0, 0, 6);
        NewOptions.Visibility = anyStart || spec.OffersTemplates ? Visibility.Visible : Visibility.Collapsed;

        TemplatesLabel.Text = Loc.Get("NewProject.Templates");
        OpenTemplates.Text = Loc.Get("NewProject.OpenTemplates");
        OpenTemplates.Visibility = spec.TemplatesDirectory is null ? Visibility.Collapsed : Visibility.Visible;
        TemplateChips.Children.Clear();
        _templateChips.Clear();
        foreach (string name in spec.Templates)
            _templateChips.Add(AddChip(TemplateChips, name, spec.Initial.Templates.Contains(name, StringComparer.OrdinalIgnoreCase)));
        TemplateChips.Visibility = spec.Templates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoTemplates.Text = Loc.Get("NewProject.NoTemplates");
        NoTemplates.Visibility = spec.OffersTemplates && spec.Templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PromptLabel.Text = Loc.Get("NewProject.PromptSection");
    }

    ToggleButton AddChip(Panel panel, string text, bool on)
    {
        var chip = new ToggleButton { Style = (Style)FindResource("Chip"), Content = text, IsChecked = on };
        // A click takes the focus; give it back to the typing once the click is done. Space on a focused option keeps it there.
        chip.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(RestoreTypingFocus, System.Windows.Threading.DispatcherPriority.Input);
        panel.Children.Add(chip);
        return chip;
    }

    void ResetNewProject()
    {
        _newProject = null;
        _promptChip = _gitChip = _planChip = null;
        _templateChips.Clear();
        StartChips.Children.Clear();
        TemplateChips.Children.Clear();
        NewOptions.Visibility = Visibility.Collapsed;
        PromptLabel.Visibility = Visibility.Collapsed;
    }

    bool PromptOn => _promptChip?.IsChecked == true;

    /// <summary>The prompt box is there only while the Prompt option is on.</summary>
    void UpdateNewProjectLayout()
    {
        if (_newProject is null)
            return;
        PromptBox.Tag = Loc.Format("Input.PromptPlaceholder", _newProject.AgentName);
        PromptLabel.Visibility = PromptOn ? Visibility.Visible : Visibility.Collapsed;
        PromptBox.Visibility = PromptOn ? Visibility.Visible : Visibility.Collapsed;
        if (PromptOn)
            AlignPlaceholder(PromptBox);
        else if (PromptBox.IsKeyboardFocused)
            Keyboard.Focus(NameBox);
        UpdateNewProjectHint();
    }

    void UpdateNewProjectHint()
    {
        if (PromptBox.IsKeyboardFocused)
            SetMessage(Loc.Get("Input.HintNewPrompt"), isError: false);
        else
            UpdateNameFeedback();
    }

    string NewProjectNameHint() => Loc.Get(PromptOn ? "Input.HintNewNext" : "Input.HintNew");

    /// <summary>Where typing goes on: the name until there is one, then the prompt when there is one.</summary>
    void RestoreTypingFocus()
    {
        if (_newProject is null)
            return;
        if (NameBox.Text.Length > 0 && PromptOn)
            FocusPrompt();
        else
            Keyboard.Focus(NameBox);
    }

    void OnNewProjectKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseMenu(restoreFocus: true);
                break;
            case Key.Tab:
                e.Handled = true;
                MoveFocus(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
            case Key.Enter when ctrl:
                e.Handled = true;
                SubmitNewProject();
                break;
            case Key.Enter when !PromptBox.IsKeyboardFocused:
                // In the name or on an option: on to the prompt when there is one, otherwise create.
                e.Handled = true;
                if (_newProject!.ValidateName(NameBox.Text) is { } error)
                {
                    SetMessage(error, isError: true);
                    Keyboard.Focus(NameBox);
                }
                else if (PromptOn)
                {
                    FocusPrompt();
                }
                else
                {
                    SubmitNewProject();
                }
                break;
        }
        // Space on an option turns it on or off; Enter in the prompt starts a new line.
    }

    /// <summary>Tab order: the name, the start options, the templates, then the prompt.</summary>
    void MoveFocus(int step)
    {
        var order = new List<Control> { NameBox };
        order.AddRange(StartChips.Children.OfType<ToggleButton>());
        order.AddRange(_templateChips);
        if (PromptOn)
            order.Add(PromptBox);
        int current = order.FindIndex(c => c.IsKeyboardFocused);
        int next = current < 0 ? 0 : ((current + step) % order.Count + order.Count) % order.Count;
        Keyboard.Focus(order[next]);
        UpdateNewProjectHint();
    }

    void FocusPrompt()
    {
        Keyboard.Focus(PromptBox);
        PromptBox.CaretIndex = PromptBox.Text.Length;
        UpdateNewProjectHint();
    }

    void SubmitNewProject()
    {
        var spec = _newProject!;
        string name = NameBox.Text;
        if (spec.ValidateName(name) is { } error)
        {
            SetMessage(error, isError: true);
            Keyboard.Focus(NameBox);
            return;
        }
        string? prompt = null;
        if (PromptOn)
        {
            prompt = PromptBox.Text.Trim();
            if (prompt.Length == 0)
            {
                SetMessage(Loc.Get("Input.PromptEmpty"), isError: true);
                FocusPrompt();
                return;
            }
            if (prompt.Length > MaxPromptLength)
            {
                SetMessage(Loc.Format("Input.PromptTooLong", prompt.Length, MaxPromptLength), isError: true);
                return;
            }
        }
        // An option the agent cannot offer keeps its last value, for the next time another agent can.
        var options = new NewProjectOptions
        {
            Prompt = _promptChip is null ? spec.Initial.Prompt : PromptOn,
            Git = _gitChip is null ? spec.Initial.Git : _gitChip.IsChecked == true,
            Plan = _planChip is null ? spec.Initial.Plan : _planChip.IsChecked == true,
            Templates = [.. spec.Templates.Where((_, i) => _templateChips[i].IsChecked == true)],
        };
        if (spec.Submit(new NewProjectRequest(name, options, prompt)) is { } failed)
            SetMessage(failed, isError: true);
    }

    void OnOpenTemplates()
    {
        if (_newProject?.TemplatesDirectory is not { } directory)
            return;
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })?.Dispose();
            CloseMenu(restoreFocus: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"Opening the templates folder '{directory}' failed", ex);
            SetMessage(ex.Message, isError: true);
        }
    }

    void ApplyNewProjectPalette()
    {
        Color text = ((SolidColorBrush)_palette.Text).Color;
        Resources["ChipText"] = _palette.SubText;
        Resources["ChipBorder"] = new SolidColorBrush(Color.FromArgb(0x38, text.R, text.G, text.B));
        Resources["ChipOnFill"] = new SolidColorBrush(Color.FromArgb(0x2E, _palette.BrandFrom.R, _palette.BrandFrom.G, _palette.BrandFrom.B));
        Resources["ChipOnBorder"] = _palette.Accent;
        Resources["ChipOnText"] = _palette.Text;
        StartLabel.Foreground = _palette.SubText;
        PromptLabel.Foreground = _palette.SubText;
        TemplatesLabel.Foreground = _palette.SubText;
        NoTemplates.Foreground = _palette.SubText;
        OpenTemplates.Foreground = _palette.Accent;
    }

    /// <summary>Developer aid (--render-new): draws the New Project card into a bitmap without showing the window.</summary>
    internal BitmapSource RenderNewProjectPreview(NewProjectSpec spec, RondelPalette palette, Color background, string name, string? prompt)
    {
        _palette = palette;
        _glow = _palette.Glow;
        ApplyPalette();
        Width = Height = 540;
        MenuRoot.Visibility = Visibility.Hidden;
        SetUpNewProject(spec);
        _input = new InputSpec(spec.Title, spec.Glyph, spec.ParentDir, spec.AgentName, AskPrompt: false, spec.ValidateName, Submit: (_, _) => null);
        InputCard.Visibility = Visibility.Visible;
        InputCard.Opacity = 1;
        InputGlyph.Text = spec.Glyph.ToString();
        InputTitle.Text = spec.Title;
        NameBox.Visibility = Visibility.Visible;
        NameBox.Text = name;
        PromptBox.Text = prompt ?? "";
        UpdateNewProjectLayout();
        UpdateNameFeedback();
        return RenderSurface(background);
    }
}
