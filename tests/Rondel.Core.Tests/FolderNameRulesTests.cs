using Rondel.Core.Launch;

namespace Rondel.Core.Tests;

public class FolderNameRulesTests
{
    static readonly string Parent = Path.Combine(Path.GetTempPath(), "rondel-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a<b")]
    [InlineData("a:b")]
    [InlineData("tab\there")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("COM¹")]
    [InlineData("LPT0")]
    public void RejectsNamesWindowsCannotCreate(string name)
    {
        Assert.NotNull(FolderNameRules.Validate(Parent, name));
    }

    [Theory]
    [InlineData("my-project")]
    [InlineData("Proje ğüşiöç İ")]
    [InlineData("v1.2")]
    [InlineData("COM10")]
    public void AcceptsOrdinaryNames(string name)
    {
        Assert.Null(FolderNameRules.Validate(Parent, name));
    }

    [Fact]
    public void LongPathsNeedLongPathSupport()
    {
        string name = new('x', FolderNameRules.ClassicPathLimit);

        Assert.NotNull(FolderNameRules.Validate(Parent, name + "x", FolderNameRules.ClassicPathLimit));
        Assert.Null(FolderNameRules.Validate(Parent, name[..200], FolderNameRules.LongPathLimit));
    }
}
