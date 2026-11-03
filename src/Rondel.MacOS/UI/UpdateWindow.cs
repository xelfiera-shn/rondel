using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Rondel.Core.Localization;
using Rondel.Core.Updates;

namespace Rondel.MacOS.UI;

/// <summary>
/// Tells about a newer release and updates on the user's word, the counterpart of the Windows UpdateWindow: what is
/// new and the choice first, then the update's progress, then what went wrong if anything did. The work itself is the
/// host's; this window only shows it.
/// </summary>
internal sealed class UpdateWindow : Window
{
    enum Stage { Offer, Working, Failed }

    readonly TextBlock _explain = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    readonly TextBlock _progressText = new() { FontSize = 12 };
    readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel _progressPanel = new();
    readonly Button _skip = new() { HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 5) };
    readonly Button _secondary = new() { HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 5), MinWidth = 80 };
    readonly Button _primary = new() { HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 5), MinWidth = 110 };
    readonly AppVersion _current;
    Stage _stage;
    bool _canRetry;
    PageLook _look = PageLook.Dark;

    public UpdateWindow(ReleaseInfo release, AppVersion current)
    {
        Release = release;
        _current = current;
        Width = 520;
        Height = 560;
        MinWidth = 440;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily(".AppleSystemUIFont");
        _progressPanel.Children.Add(_progressText);
        _progressPanel.Children.Add(_progress);
        _primary.Classes.Add("accent");
        _primary.Click += (_, _) => OnPrimary();
        _secondary.Click += (_, _) => OnSecondary();
        _skip.Click += (_, _) =>
        {
            SkipRequested?.Invoke();
            Close();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _stage != Stage.Working)
                Close();
        };
        // Closing the window while it downloads cancels the download.
        Closing += (_, _) =>
        {
            if (_stage == Stage.Working)
                CancelRequested?.Invoke();
        };
        ActualThemeVariantChanged += (_, _) => Build();
        Build();
        ShowOffer();
    }

    public ReleaseInfo Release { get; }

    /// <summary>"Update now", or "Try again" after a failure that may pass.</summary>
    public event Action? UpdateRequested;

    /// <summary>"Cancel" while downloading, or the window closed then.</summary>
    public event Action? CancelRequested;

    public event Action? SkipRequested;

    public event Action? ReleasePageRequested;

    /// <summary>What is new, and the choice: skip this version, later, or update now.</summary>
    public void ShowOffer()
    {
        _stage = Stage.Offer;
        ShowOnly(_explain);
        _skip.IsVisible = true;
        SetButton(_secondary, Loc.Get("Update.Later"));
        SetButton(_primary, Loc.Get("Update.Now"));
    }

    /// <summary>The update is under way: downloading (<paramref name="fraction"/> done) or restarting (no fraction).</summary>
    public void ShowWorking(string text, double? fraction, bool canCancel)
    {
        _stage = Stage.Working;
        ShowOnly(_progressPanel);
        _progressText.Text = text;
        _progress.IsIndeterminate = fraction is null;
        _progress.Value = fraction ?? 0;
        _skip.IsVisible = false;
        SetButton(_secondary, Loc.Get("Update.Cancel"), enabled: canCancel);
        SetButton(_primary, null);
    }

    /// <summary>What went wrong; "Try again" when that may help, otherwise the release page is the way.</summary>
    public void ShowFailed(string message, bool canRetry)
    {
        _stage = Stage.Failed;
        _canRetry = canRetry;
        ShowOnly(_error);
        _error.Text = message;
        _skip.IsVisible = false;
        SetButton(_secondary, Loc.Get("Update.Close"));
        SetButton(_primary, Loc.Get(canRetry ? "Update.TryAgain" : "Update.ReleasePage"));
    }

    void Build()
    {
        _look = ActualThemeVariant == ThemeVariant.Light ? PageLook.Light : PageLook.Dark;
        Background = _look.Background;
        Title = Loc.Format("Update.Title", Release.Version);
        foreach (var text in new[] { _explain, _progressText })
            text.Foreground = _look.Text;
        _error.Foreground = _look.Text;
        _explain.Text = Loc.Get("Update.Explain");
        _skip.Content = Loc.Get("Update.Skip");
        foreach (var control in new Control[] { _explain, _error, _progressPanel, _skip, _secondary, _primary })
            (control.Parent as Panel)?.Children.Remove(control);

        var header = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 0),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        PageLook.Logo(),
                        new StackPanel
                        {
                            Margin = new Thickness(12, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = Loc.Format("Update.Title", Release.Version), FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = _look.Text },
                                new TextBlock
                                {
                                    FontSize = 12,
                                    Foreground = _look.SubText,
                                    Text = Release.Published is { } published
                                        ? Loc.Format("Update.Subtitle", _current, published.ToLocalTime().ToString("d MMMM yyyy", Loc.Culture))
                                        : Loc.Format("Update.SubtitleNoDate", _current),
                                },
                            },
                        },
                    },
                },
                new Border { Height = 2, Margin = new Thickness(0, 14, 0, 0), CornerRadius = new CornerRadius(1), Background = _look.Brand },
            },
        };

        // The notes without the lines only about the Windows app.
        var sections = ReleaseNotes.Parse(Release.Notes, withoutScope: "windows");
        var notes = new StackPanel();
        foreach (var section in sections)
        {
            if (section.Heading.Length > 0)
            {
                notes.Children.Add(new TextBlock
                {
                    Text = section.Heading, FontWeight = FontWeight.SemiBold, Foreground = _look.Text,
                    Margin = new Thickness(0, notes.Children.Count == 0 ? 0 : 12, 0, 4),
                });
            }
            foreach (string line in section.Lines)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*"), Margin = new Thickness(0, 2) };
                row.Children.Add(new TextBlock { Text = "•", Foreground = _look.SubText });
                var text = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, Foreground = _look.Text };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                notes.Children.Add(row);
            }
        }
        var notesCard = new Border
        {
            Padding = new Thickness(16, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = _look.Card,
            BorderBrush = _look.CardStroke,
            IsVisible = sections.Count > 0,
            Child = new ScrollViewer { Content = notes, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24, 16, 24, 0) };
        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("Update.WhatsNew"), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = _look.Text,
            Margin = new Thickness(2, 0, 0, 8), IsVisible = sections.Count > 0,
        });
        Grid.SetRow(notesCard, 1);
        body.Children.Add(notesCard);
        var status = new Panel { Margin = new Thickness(0, 12, 0, 0), Children = { _explain, _error, _progressPanel } };
        Grid.SetRow(status, 2);
        body.Children.Add(status);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _secondary, _primary } };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(24, 16, 24, 20) };
        footer.Children.Add(_skip);
        Grid.SetColumn(right, 1);
        footer.Children.Add(right);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(header);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }

    void ShowOnly(Control shown)
    {
        foreach (var element in new Control[] { _explain, _progressPanel, _error })
            element.IsVisible = element == shown;
    }

    static void SetButton(Button button, string? text, bool enabled = true)
    {
        button.IsVisible = text is not null;
        button.Content = text;
        button.IsEnabled = enabled;
    }

    void OnPrimary()
    {
        if (_stage == Stage.Offer || (_stage == Stage.Failed && _canRetry))
            UpdateRequested?.Invoke();
        else if (_stage == Stage.Failed)
            ReleasePageRequested?.Invoke();
    }

    void OnSecondary()
    {
        if (_stage == Stage.Working)
            CancelRequested?.Invoke();
        else
            Close(); // Later, or Close after a failure
    }
}
