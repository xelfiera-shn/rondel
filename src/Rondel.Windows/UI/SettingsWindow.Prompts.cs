using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Rondel.Core.Localization;
using Rondel.Core.Prompts;
using Rondel.Core.Settings;

namespace Rondel.Windows.UI;

/// <summary>
/// Saved prompts: whether the Ask slices offer them, and which of them are pinned to the slices' arc. The list is the
/// prompts folder as it is now, read again whenever the window comes to the front.
/// </summary>
public partial class SettingsWindow
{
    void LoadPrompts(AppSettings settings)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            PromptsBox.IsChecked = settings.Prompts.Enabled;
            QuickCardBox.IsChecked = settings.Quick.PromptCard;
            PromptsPanel.IsEnabled = settings.Prompts.Enabled;
            PromptsDesc.Text = Loc.Format("Settings.Prompts.List.Desc", SavedPrompts.MaxPinned);

            var names = _host.Prompts.List();
            var pinned = _host.Prompts.Existing(settings.Prompts.Pinned).ToList();
            PromptRows.Children.Clear();
            foreach (string name in names)
            {
                int place = pinned.FindIndex(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
                PromptRows.Children.Add(PromptRow(name, place, canPin: place >= 0 || pinned.Count < SavedPrompts.MaxPinned, first: PromptRows.Children.Count == 0));
            }
            PromptRows.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PinnedCount.Text = Loc.Format("Settings.Prompts.Pinned", pinned.Count, SavedPrompts.MaxPinned);
            PinnedCount.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoPrompts.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>A prompt's row: its place on the arc when pinned, its name, and the pin.</summary>
    /// <param name="place">0-based place on the arc, or -1 when not pinned.</param>
    FrameworkElement PromptRow(string name, int place, bool canPin, bool first)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (place >= 0)
        {
            var badge = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = (place + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
            grid.Children.Add(badge);
        }

        var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = name };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        bool isPinned = place >= 0;
        var pin = new ToggleButton
        {
            IsChecked = isPinned,
            IsEnabled = canPin,
            Width = 36,
            Height = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(12, 0, 0, 0),
            ToolTip = Loc.Get(isPinned ? "Settings.Prompts.Unpin" : "Settings.Prompts.Pin"),
            Content = new TextBlock
            {
                Text = isPinned ? "" : "", // PinnedFill, Pin
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
            },
        };
        AutomationName(pin, Loc.Get(isPinned ? "Settings.Prompts.Unpin" : "Settings.Prompts.Pin") + ": " + name);
        pin.Click += (_, _) => SetPinned(name, pin.IsChecked == true);
        Grid.SetColumn(pin, 2);
        grid.Children.Add(pin);

        var row = new Border { Padding = new Thickness(0, 6, 0, 6), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Child = grid };
        row.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return row;
    }

    static void AutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    /// <summary>Pins a prompt at the end of the arc or takes it off; pins whose files are gone are dropped on the way.</summary>
    void SetPinned(string name, bool pin)
    {
        if (_loading)
            return;
        var pinned = _host.Prompts.Existing(Store.Current.Prompts.Pinned)
            .Where(p => !string.Equals(p, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pin && pinned.Count < SavedPrompts.MaxPinned)
            pinned.Add(name);
        Store.Update(s => s.Prompts.Pinned = pinned);
        LoadPrompts(Store.Current);
    }

    void OnQuickCardClicked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        bool card = QuickCardBox.IsChecked == true;
        Store.Update(s => s.Quick.PromptCard = card);
    }

    void OnPromptsClicked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        bool enabled = PromptsBox.IsChecked == true;
        Store.Update(s => s.Prompts.Enabled = enabled);
        PromptsPanel.IsEnabled = enabled;
    }

    void OnOpenPrompts(object sender, RoutedEventArgs e) => _host.OpenPromptsFolder();
}
