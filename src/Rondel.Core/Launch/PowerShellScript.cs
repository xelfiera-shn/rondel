using System.Text;
using Rondel.Core.Activity;
using Rondel.Core.Localization;

namespace Rondel.Core.Launch;

/// <summary>
/// Builds the PowerShell script that runs a <see cref="LaunchPlan"/>. Everything (folder, prompt,
/// messages) travels inside this one script, which the launcher passes with -EncodedCommand, so
/// quotes, ';', '$', backticks, line breaks and Turkish characters survive every layer of parsing.
/// </summary>
public static class PowerShellScript
{
    public static string Build(LaunchPlan plan)
    {
        var (name, extra) = CommandLine.Split(plan.Command);

        var s = new StringBuilder();
        s.AppendLine("$ErrorActionPreference = 'Continue'");
        s.AppendLine($"try {{ Set-Location -LiteralPath {Quote(plan.Directory)} -ErrorAction Stop }}");
        s.AppendLine($"catch {{ Write-Host ({Quote(Loc.Format("Launch.NoFolder", plan.Directory))}) -ForegroundColor Red; return }}");
        s.AppendLine($"try {{ $Host.UI.RawUI.WindowTitle = {Quote(plan.Title)} }} catch {{ }}");
        if (plan.ActivityId is { } id)
        {
            // The agent's hooks send it back with every event, so Rondel knows which session reported.
            s.AppendLine($"$env:{AgentActivity.EnvironmentVariable} = {Quote(id)}");
        }
        if (plan.InitGit)
        {
            // A new project gets its repository before the agent starts; without git it starts anyway.
            s.AppendLine("if (Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue) { & git init } "
                + $"else {{ Write-Host ({Quote(Loc.Get("Launch.NoGit"))}) -ForegroundColor Yellow }}");
        }
        s.AppendLine($"$rondelName = {Quote(name)}");
        s.AppendLine($"$rondelArgs = @({string.Join(", ", plan.Arguments.Select(Quote))})");
        s.AppendLine("$rondelCommand = Get-Command -Name $rondelName -ErrorAction SilentlyContinue | Select-Object -First 1");
        s.AppendLine("if (-not $rondelCommand) {");
        s.AppendLine("    Write-Host ''");
        s.AppendLine($"    Write-Host ({Quote(Loc.Format("Launch.NotFound", name))}) -ForegroundColor Red");
        s.AppendLine($"    Write-Host ({Quote(Loc.Format("Launch.NotFoundHint", plan.AgentName))}) -ForegroundColor DarkGray");
        s.AppendLine("    return");
        s.AppendLine("}");
        s.AppendLine("$rondelTarget = $rondelCommand");
        s.AppendLine("while ($rondelTarget.CommandType -eq 'Alias' -and $rondelTarget.ResolvedCommand) { $rondelTarget = $rondelTarget.ResolvedCommand }");
        // An npm install exposes the agent as a .cmd file: cmd.exe cannot carry line breaks or quotes in one argument.
        s.AppendLine("$rondelBatch = $rondelTarget.CommandType -eq 'Application' -and $rondelTarget.Extension -in '.cmd', '.bat'");
        s.AppendLine("if ($rondelBatch -and ($rondelArgs | Where-Object { $_ -match '[\\r\\n\"%]' })) {");
        s.AppendLine($"    Write-Host ({Quote(Loc.Format("Launch.BatchNote", name))}) -ForegroundColor Yellow");
        s.AppendLine("    $rondelArgs = @($rondelArgs | ForEach-Object { ($_ -replace '\\r?\\n', ' ') -replace '\"', \"'\" })");
        s.AppendLine("}");
        // Windows PowerShell (and pwsh < 7.3 / Legacy mode) passes embedded quotes to native programs unescaped.
        // Escape them the way CommandLineToArgvW expects; pwsh 7.3+ does this itself.
        s.AppendLine("if ($rondelBatch -or $PSVersionTable.PSVersion -lt [version]'7.3' -or $PSNativeCommandArgumentPassing -eq 'Legacy') {");
        s.AppendLine("    $rondelArgs = @($rondelArgs | ForEach-Object {");
        s.AppendLine("        $rondelArg = $_ -replace '(\\\\*)\"', '$1$1\\\"'");
        s.AppendLine("        if ($rondelArg -match '\\s') { $rondelArg = $rondelArg -replace '(\\\\+)$', '$1$1' }");
        s.AppendLine("        $rondelArg");
        s.AppendLine("    })");
        s.AppendLine("}");
        s.AppendLine("Remove-Variable rondelCommand, rondelTarget, rondelBatch -ErrorAction SilentlyContinue");
        // Anything after the command name is the user's own PowerShell text from settings (e.g. "--model opus").
        s.AppendLine($"& $rondelName {extra} @rondelArgs".TrimEnd());
        return s.ToString();
    }

    /// <summary>
    /// PowerShell single-quoted literal: nothing is expanded inside it. PowerShell also treats the
    /// typographic quotes ‘ ’ ‚ ‛ as single quotes, so those are doubled as well.
    /// </summary>
    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('\'');
        foreach (char c in value)
        {
            builder.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
                builder.Append(c);
        }
        return builder.Append('\'').ToString();
    }
}
