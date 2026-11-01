using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Rondel.Core.Localization;
using Rondel.Core.Updates;

namespace Rondel.Windows.UI;

/// <summary>
/// Tells about a newer release and updates on the user's word: what is new and the choice first, then the update's
/// progress, then what went wrong if anything did. The work itself is the host's; this window only shows it.
/// </summary>
public partial class UpdateWindow : Window
{
    enum Stage { Offer, Working, Failed }

    Stage _stage;
    bool _canRetry;
    bool _canCancel;

    /// <param name="mode">Light or dark; the system's by default (developer aids fix it).</param>
    internal UpdateWindow(ReleaseInfo release, AppVersion current, ThemeColors theme, ThemeMode? mode = null)
    {
        Release = release;
        ThemeMode = mode ?? ThemeMode.System; // Fluent styles, like Settings
        InitializeComponent();
        Icon = AppIconRenderer.Render(64);
        HeaderIcon.Source = AppIconRenderer.Render(72);
        ApplyBrand(theme);

        TitleText.Text = Loc.Format("Update.Title", release.Version);
        SubtitleText.Text = release.Published is { } published
            ? Loc.Format("Update.Subtitle", current, published.ToLocalTime().ToString("d MMMM yyyy", Loc.Culture))
            : Loc.Format("Update.SubtitleNoDate", current);
        FillNotes(ReleaseNotes.Parse(release.Notes));
        ShowOffer();
    }

    internal ReleaseInfo Release { get; }

    /// <summary>"Update now", or "Try again" after a failure that may pass.</summary>
    internal event Action? UpdateRequested;

    /// <summary>"Cancel" while downloading, or the window closed then.</summary>
    internal event Action? CancelRequested;

    internal event Action? SkipRequested;

    internal event Action? ReleasePageRequested;

    internal void ApplyBrand(ThemeColors theme)
    {
        FluentBrand.Apply(this, theme);
        BrandLine.Background = FluentBrand.Line(theme);
    }

    /// <summary>What is new, and the choice: skip this version, later, or update now.</summary>
    internal void ShowOffer()
    {
        _stage = Stage.Offer;
        ShowOnly(ExplainText);
        SkipButton.Visibility = Visibility.Visible;
        SetButton(SecondaryButton, Loc.Get("Update.Later"));
        SetButton(PrimaryButton, Loc.Get("Update.Now"));
    }

    /// <summary>The update is under way: downloading (<paramref name="fraction"/> done) or restarting (no fraction).</summary>
    internal void ShowWorking(string text, double? fraction, bool canCancel)
    {
        _stage = Stage.Working;
        _canCancel = canCancel;
        ShowOnly(ProgressPanel);
        ProgressText.Text = text;
        Progress.IsIndeterminate = fraction is null;
        Progress.Value = fraction ?? 0;
        SkipButton.Visibility = Visibility.Collapsed;
        SetButton(SecondaryButton, Loc.Get("Update.Cancel"), enabled: canCancel);
        SetButton(PrimaryButton, null);
    }

    /// <summary>What went wrong; "Try again" when that may help, otherwise the release page is the way.</summary>
    internal void ShowFailed(string message, bool canRetry)
    {
        _stage = Stage.Failed;
        _canRetry = canRetry;
        ShowOnly(ErrorText);
        ErrorText.Text = message;
        SkipButton.Visibility = Visibility.Collapsed;
        SetButton(SecondaryButton, Loc.Get("Update.Close"));
        SetButton(PrimaryButton, Loc.Get(canRetry ? "Update.TryAgain" : "Update.ReleasePage"));
    }

    void FillNotes(IReadOnlyList<ReleaseNoteSection> sections)
    {
        NotesList.Children.Clear();
        NotesPanel.Visibility = sections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var section in sections)
        {
            if (section.Heading.Length > 0)
            {
                NotesList.Children.Add(new TextBlock
                {
                    Text = section.Heading,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, NotesList.Children.Count == 0 ? 0 : 12, 0, 4),
                });
            }
            foreach (string line in section.Lines)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var bullet = new TextBlock { Text = "•" };
                bullet.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                var text = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, 1);
                row.Children.Add(bullet);
                row.Children.Add(text);
                NotesList.Children.Add(row);
            }
        }
    }

    void ShowOnly(FrameworkElement shown)
    {
        foreach (var element in new FrameworkElement[] { ExplainText, ProgressPanel, ErrorText })
            element.Visibility = element == shown ? Visibility.Visible : Visibility.Collapsed;
    }

    static void SetButton(Button button, string? text, bool enabled = true)
    {
        button.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        button.Content = text;
        button.IsEnabled = enabled;
    }

    void OnPrimary(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Offer || (_stage == Stage.Failed && _canRetry))
            UpdateRequested?.Invoke();
        else if (_stage == Stage.Failed)
            ReleasePageRequested?.Invoke();
    }

    void OnSecondary(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Working)
            CancelRequested?.Invoke();
        else
            Close(); // Later, or Close after a failure
    }

    void OnSkip(object sender, RoutedEventArgs e)
    {
        SkipRequested?.Invoke();
        Close();
    }

    void OnReleasePage(object sender, RoutedEventArgs e) => ReleasePageRequested?.Invoke();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closed while downloading: the download stops. While restarting, it is too late to stop, and nothing is lost.
        if (_stage == Stage.Working && _canCancel)
            CancelRequested?.Invoke();
        base.OnClosing(e);
    }
}
