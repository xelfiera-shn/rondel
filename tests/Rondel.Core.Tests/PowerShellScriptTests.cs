using Rondel.Core.Launch;

namespace Rondel.Core.Tests;

public class PowerShellScriptTests
{
    [Fact]
    public void QuoteDoublesStraightAndTypographicSingleQuotes()
    {
        Assert.Equal("'it''s ‘‘x’’ ‚‚y‛‛ $HOME `n'", PowerShellScript.Quote("it's ‘x’ ‚y‛ $HOME `n"));
    }

    [Fact]
    public void BuildKeepsEveryValueInsideSingleQuotedLiterals()
    {
        var plan = new LaunchPlan(@"C:\Projects\O'Brien $x", "Claude Code", "claude --model opus", ["say \"hi\"; rm `$HOME", "ikinci satır\nson"]);

        string script = PowerShellScript.Build(plan);

        Assert.Contains("Set-Location -LiteralPath 'C:\\Projects\\O''Brien $x' -ErrorAction Stop", script);
        Assert.Contains("$rondelName = 'claude'", script);
        Assert.Contains("$rondelArgs = @('say \"hi\"; rm `$HOME', 'ikinci satır\nson')", script);
        Assert.EndsWith("& $rondelName --model opus @rondelArgs" + Environment.NewLine, script);
    }

    [Fact]
    public void BuildCreatesTheRepositoryOnlyWhenAsked()
    {
        var plan = new LaunchPlan(Path.GetTempPath(), "Claude Code", "claude", []);

        Assert.DoesNotContain("git init", PowerShellScript.Build(plan));
        Assert.Contains("{ & git init }", PowerShellScript.Build(plan with { InitGit = true }));
    }

    [Fact]
    public void BuildPutsTheFollowedSessionsIdIntoTheEnvironment()
    {
        var plan = new LaunchPlan(Path.GetTempPath(), "Claude Code", "claude", []);

        Assert.DoesNotContain("RONDEL_SESSION", PowerShellScript.Build(plan));
        Assert.Contains("$env:RONDEL_SESSION = '0123456789ab'", PowerShellScript.Build(plan with { ActivityId = "0123456789ab" }));
    }

    [Theory]
    [InlineData("claude", "claude", "")]
    [InlineData("  claude --model opus ", "claude", "--model opus")]
    [InlineData("\"C:\\Tools\\my agent.exe\" -x", "C:\\Tools\\my agent.exe", "-x")]
    public void CommandLineSplitSeparatesTheProgramFromTheRest(string commandLine, string name, string rest)
    {
        Assert.Equal((name, rest), CommandLine.Split(commandLine));
    }
}
