using System.Text.RegularExpressions;
using Rondel.Core.Localization;

namespace Rondel.Core.Updates;

/// <summary>A section of a release's notes: its heading and its lines.</summary>
public sealed record ReleaseNoteSection(string Heading, IReadOnlyList<string> Lines);

/// <summary>A release's notes for people, without the scopes and pull request numbers the changelog carries.</summary>
public static partial class ReleaseNotes
{
    /// <summary>The section the release workflow puts the macOS app's features and fixes in (cliff.toml).</summary>
    public const string MacHeading = "macOS";

    /// <summary>
    /// Reads the notes the release workflow writes (git-cliff): "### Features" headings over
    /// "- **scope:** Summary (#12)" lines. Known headings are shown in the UI language; the lines stay as written.
    /// </summary>
    public static IReadOnlyList<ReleaseNoteSection> Parse(string markdown) =>
        [.. Sections(markdown).Select(section => section with { Heading = Heading(section.Heading) })];

    /// <summary>
    /// Whether every section with lines in the notes is <paramref name="heading"/>: a release that changes only the
    /// macOS app, for example. False for notes without any section, which say nothing either way.
    /// </summary>
    public static bool OnlyAbout(string markdown, string heading)
    {
        var sections = Sections(markdown);
        return sections.Count > 0 && sections.All(section => section.Heading == heading);
    }

    /// <summary>The sections with their headings as written.</summary>
    static List<ReleaseNoteSection> Sections(string markdown)
    {
        var sections = new List<ReleaseNoteSection>();
        string heading = "";
        var lines = new List<string>();
        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('#'))
            {
                Flush();
                heading = line.TrimStart('#').Trim();
            }
            else if ((line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) && Clean(line[2..]) is { Length: > 0 } item)
            {
                lines.Add(item);
            }
        }
        Flush();
        return sections;

        void Flush()
        {
            if (lines.Count > 0)
                sections.Add(new ReleaseNoteSection(heading, lines.ToList()));
            lines.Clear();
        }
    }

    static string Heading(string text) => text switch
    {
        "Features" => Loc.Get("Update.Notes.Features"),
        "Bug fixes" => Loc.Get("Update.Notes.Fixes"),
        "Performance" => Loc.Get("Update.Notes.Performance"),
        _ => text,
    };

    /// <summary>"**ui:** Choose the menu's theme (#30)" → "Choose the menu's theme".</summary>
    static string Clean(string text)
    {
        text = Scope().Replace(text, "");
        text = PullRequest().Replace(text, "");
        return text.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal).Trim();
    }

    [GeneratedRegex(@"^\*\*[^*]+:\*\*\s*")]
    private static partial Regex Scope();

    [GeneratedRegex(@"\s*\(#\d+\)$")]
    private static partial Regex PullRequest();
}
