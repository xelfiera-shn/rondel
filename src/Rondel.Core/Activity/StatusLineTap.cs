using System.Text.Encodings.Web;
using System.Text.Json;

namespace Rondel.Core.Activity;

/// <summary>
/// The status line Rondel gives the sessions it starts while the agents card shows the usage limits. Claude Code puts
/// the limits only in a status line's input, so Rondel's executable runs as that status line: it keeps the limits in
/// <see cref="UsageStore"/>, and hands the input on to the user's own status line, which runs after it in a pipe and so
/// shows as before. Without one of the user's, it prints nothing, which leaves no empty line.
/// </summary>
/// <remarks>
/// Claude Code runs a status line command in Git Bash, or in PowerShell where Git Bash is missing, so the command must
/// work in both: the executable's path unquoted (<see cref="ShellPath"/>), and an argument that tells which shell runs
/// it, as "$PSHOME" is empty in Bash. Bash pipes bytes as they are. Windows PowerShell 5.1 turns what is not ASCII into
/// "?" between two programs, so there the input goes on as JSON with every other character escaped (\uXXXX): the same
/// JSON to a script that reads it as JSON.
/// </remarks>
public static class StatusLineTap
{
    /// <summary>Runs Rondel's executable as the tap, before anything else starts.</summary>
    public const string Argument = "--status-line";

    /// <summary>The user's status line follows in the pipe: hand the input on.</summary>
    public const string PassArgument = "--pass";

    const string ShellArgument = "--ps=";

    /// <summary>A status line's input is a few kilobytes; anything far beyond that is not one.</summary>
    const int MaxInput = 4 * 1024 * 1024;

    /// <summary>The start of the status line command, for the executable at <paramref name="shellPath"/> (<see cref="ShellPath"/>).</summary>
    public static string Prefix(string shellPath) => $"{shellPath} {Argument} \"{ShellArgument}$PSHOME\"";

    /// <summary>
    /// The executable's path as Git Bash and PowerShell both run it unquoted: with forward slashes, and only letters,
    /// digits and <c>:/._-~</c>. A path with other characters (a space) is tried in its short form; null when neither
    /// form will do.
    /// </summary>
    /// <param name="shortPath">The path's short (8.3) form, where the file system has one.</param>
    public static string? ShellPath(string path, Func<string, string?>? shortPath = null)
    {
        if (IsPlain(path))
            return path.Replace('\\', '/');
        return shortPath?.Invoke(path) is { } brief && IsPlain(brief) ? brief.Replace('\\', '/') : null;
    }

    static bool IsPlain(string path) =>
        path.Length > 0 && path.All(c => char.IsLetterOrDigit(c) || c is '\\' or '/' or ':' or '.' or '_' or '-' or '~');

    /// <summary>
    /// Runs the tap: reads Claude Code's input, hands it on when the user's status line follows, and keeps the usage
    /// limits in it. Never throws: a status line that fails would only leave the user's line empty.
    /// </summary>
    /// <returns>The exit code, always 0.</returns>
    public static int Run(IReadOnlyList<string> args, Stream input, Stream output, UsageStore store, TimeProvider? time = null)
    {
        byte[] json;
        try
        {
            json = ReadAll(input);
        }
        catch (IOException)
        {
            return 0;
        }
        try
        {
            if (args.Contains(PassArgument))
            {
                bool powerShell = args.FirstOrDefault(a => a.StartsWith(ShellArgument, StringComparison.Ordinal)) is { Length: > 5 };
                output.Write(powerShell ? Ascii(json) : json);
                output.Flush();
            }
        }
        catch (IOException)
        {
            // The user's status line went away (Claude Code stops a status line that a newer one replaces).
        }
        finally
        {
            try
            {
                output.Close();
            }
            catch (IOException)
            {
            }
        }
        try
        {
            if (UsageLimits.FromStatusLine(json, (time ?? TimeProvider.System).GetUtcNow()) is { } limits)
                store.Report(limits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return 0;
    }

    static byte[] ReadAll(Stream input)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = input.Read(chunk)) > 0)
        {
            if (buffer.Length + read > MaxInput)
                throw new IOException("The status line's input is too long");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>The same JSON with only ASCII in it; the input as it is when it is not JSON.</summary>
    internal static byte[] Ascii(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
                document.WriteTo(writer);
            buffer.WriteByte((byte)'\n');
            return buffer.ToArray();
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
