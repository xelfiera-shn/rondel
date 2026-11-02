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
using Rondel.Core.Prompts;

namespace Rondel.Windows.UI;

/// <summary>
/// Saved prompts on the Ask card: a chip per prompt above the prompt box. Picking one puts the prompt in the box, to
/// edit or to start as it is. With the keyboard, Tab moves between the chips and the box, Space picks the chip and
/// Enter goes on to the box; Ctrl+Enter starts from anywhere on the card.
/// </summary>
public partial class RadialMenuWindow
{
    readonly List<ToggleButton> _savedPromptChips = [];

    /// <summary>Shows the saved prompts the card offers, if any are offered.</summary>
    void SetUpSavedPrompts(InputSpec spec)
    {
        ResetSavedPrompts();
        if (spec.SavedPrompts is null || spec.AskName)
            return;
        SavedPromptsLabel.Text = Loc.Get("Input.SavedPrompts");
        OpenPrompts.Text = Loc.Get("Prompts.Open");
        foreach (string name in spec.PromptNames)
        {
            var chip = AddChip(SavedPromptChips, name, on: false);
            // Long names end in an ellipsis rather than leaving the card; the whole name is in the tooltip.
            chip.Content = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 290 };
            chip.ToolTip = name;
            chip.Click += (_, _) => UseSavedPrompt(chip, name);
            chip.GotKeyboardFocus += (_, _) => SetMessage(Loc.Get("Input.HintSavedPrompt"), isError: false);
            _savedPromptChips.Add(chip);
        }
        SavedPromptChips.Visibility = spec.PromptNames.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoSavedPrompts.Text = Loc.Get("Input.NoSavedPrompts");
        NoSavedPrompts.Visibility = spec.PromptNames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedPromptsPanel.Visibility = Visibility.Visible;
    }

    void ResetSavedPrompts()
    {
        _savedPromptChips.Clear();
        SavedPromptChips.Children.Clear();
        SavedPromptsPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Puts the prompt in the box; its chip stays lit while the box holds what it started from.</summary>
    void UseSavedPrompt(ToggleButton chip, string name)
    {
        if (_input?.SavedPrompts?.Read(name) is not { } text)
        {
            chip.IsChecked = false;
            SetMessage(Loc.Format("Prompts.Unusable", name, SavedPrompts.MaxBytes / 1024), isError: true);
            return;
        }
        foreach (var other in _savedPromptChips)
            other.IsChecked = ReferenceEquals(other, chip);
        PromptBox.Text = text;
        PromptBox.CaretIndex = text.Length;
        PromptBox.ScrollToEnd();
    }

    /// <summary>A cleared box has no saved prompt in it any more.</summary>
    void OnPromptTextChanged()
    {
        if (PromptBox.Text.Length == 0)
        {
            foreach (var chip in _savedPromptChips)
                chip.IsChecked = false;
        }
    }

    /// <summary>
    /// Tab, Shift+Tab and Enter while the card offers saved prompts; true when the key was taken. Enter on a chip goes on to
    /// the box (Space picks the chip, Ctrl+Enter starts).
    /// </summary>
    bool OnSavedPromptsKeyDown(KeyEventArgs e)
    {
        if (_savedPromptChips.Count == 0)
            return false;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Tab:
                var order = new List<Control>(_savedPromptChips) { PromptBox };
                int current = order.FindIndex(c => c.IsKeyboardFocused);
                int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                int next = current < 0 ? order.Count - 1 : ((current + step) % order.Count + order.Count) % order.Count;
                FocusOnCard(order[next]);
                return true;
            case Key.Enter when !ctrl && _savedPromptChips.Any(c => c.IsKeyboardFocused):
                FocusOnCard(PromptBox);
                return true;
            default:
                return false;
        }
    }

    void FocusOnCard(Control target)
    {
        Keyboard.Focus(target);
        if (target != PromptBox)
            return;
        PromptBox.CaretIndex = PromptBox.Text.Length;
        SetMessage(Loc.Get(_input?.AskName == true ? "Input.HintPrompt" : "Input.HintPromptOnly"), isError: false);
    }

    void OnOpenPrompts()
    {
        if (_input?.SavedPrompts?.Directory is not { } directory)
            return;
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true })?.Dispose();
            CloseMenu(restoreFocus: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"Opening the prompts folder '{directory}' failed", ex);
            SetMessage(ex.Message, isError: true);
        }
    }

    void ApplySavedPromptsPalette()
    {
        SavedPromptsLabel.Foreground = _palette.SubText;
        NoSavedPrompts.Foreground = _palette.SubText;
        OpenPrompts.Foreground = _palette.Accent;
    }

    /// <summary>Developer aid (--render-ask): draws the Ask card into a bitmap without showing the window.</summary>
    /// <param name="picked">A saved prompt to pick, as a click would; null for the empty box.</param>
    internal BitmapSource RenderAskPreview(InputSpec spec, RondelPalette palette, Color background, string? picked)
    {
        _palette = palette;
        _glow = _palette.Glow;
        ApplyPalette();
        Width = Height = 540;
        MenuRoot.Visibility = Visibility.Hidden;
        _input = spec;
        InputCard.Visibility = Visibility.Visible;
        InputCard.Opacity = 1;
        InputGlyph.Text = spec.Glyph.ToString();
        PromptBox.Tag = Loc.Format("Input.PromptPlaceholder", spec.AgentName);
        PromptBox.Text = "";
        SetUpSavedPrompts(spec);
        ShowPromptStage(name: null);
        int index = picked is null ? -1 : spec.PromptNames.ToList().IndexOf(picked);
        if (index >= 0)
            UseSavedPrompt(_savedPromptChips[index], picked!);
        return RenderSurface(background);
    }
}
