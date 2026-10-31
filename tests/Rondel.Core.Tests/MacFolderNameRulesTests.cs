using Rondel.Core.Launch;

namespace Rondel.Core.Tests;

public class MacFolderNameRulesTests
{
    static readonly string Parent = Path.Combine(Path.GetTempPath(), "rondel-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("tab\there")]
    [InlineData(".hidden")]
    public void RejectsNamesMacOSCannotCreateOrWouldHide(string name)
    {
        Assert.NotNull(MacFolderNameRules.Validate(Parent, name));
    }

    [Theory]
    [InlineData("my-project")]
    [InlineData("Proje ğüşiöç İ")]
    [InlineData("v1.2")]
    [InlineData("CON")] // reserved only on Windows
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("a<b>c|d*e?f\"g\\h")]
    public void AcceptsWhatWindowsWouldRefuse(string name)
    {
        Assert.Null(MacFolderNameRules.Validate(Parent, name));
    }

    [Fact]
    public void TheLimitIsInBytesNotCharacters()
    {
        Assert.Null(MacFolderNameRules.Validate(Parent, new string('a', 255)));
        Assert.NotNull(MacFolderNameRules.Validate(Parent, new string('a', 256)));
        // ğ takes two bytes in UTF-8: 128 of them are 256 bytes.
        Assert.Null(MacFolderNameRules.Validate(Parent, new string('ğ', 127)));
        Assert.NotNull(MacFolderNameRules.Validate(Parent, new string('ğ', 128)));
    }

    [Fact]
    public void AnExistingFolderIsRejected()
    {
        string parent = Directory.CreateDirectory(Parent).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(parent, "taken"));

            Assert.NotNull(MacFolderNameRules.Validate(parent, "taken"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }
}
