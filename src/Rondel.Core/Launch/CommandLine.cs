namespace Rondel.Core.Launch;

public static class CommandLine
{
    /// <summary>
    /// Splits a configured command such as "claude --model opus", "cc" or "\"C:\Tools\my agent.exe\" -x"
    /// into the program (or alias) and the user's own text after it.
    /// </summary>
    public static (string Name, string Extra) Split(string commandLine)
    {
        string trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            return end > 0 ? (trimmed[1..end], trimmed[(end + 1)..].Trim()) : (trimmed.Trim('"'), "");
        }
        int space = trimmed.IndexOfAny([' ', '\t']);
        return space > 0 ? (trimmed[..space], trimmed[space..].Trim()) : (trimmed, "");
    }
}
