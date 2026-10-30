using System.Text.Json;
using Rondel.Core.Agents.ClaudeCode;

namespace Rondel.Core.Tests;

public class ClaudeCodeSessionStoreTests
{
    [Fact]
    public void EncodeReplacesEverythingButAsciiLettersAndDigits()
    {
        Assert.Equal("C--Users-Ahmet-Belgeler-Proje-----v2", ClaudeCodeSessionStore.Encode(@"C:\Users\Ahmet\Belgeler\Proje Ğüş.v2"));
    }

    [Fact]
    public void LookupCountsOnlySessionsRecordedForTheFolder()
    {
        var root = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            string target = Path.Combine(root.FullName, "work", "a.b");
            string collidingFolder = Path.Combine(root.FullName, "work", "a-b"); // encodes to the same name
            string projects = Path.Combine(root.FullName, "projects");
            string sessions = Directory.CreateDirectory(Path.Combine(projects, ClaudeCodeSessionStore.Encode(target))).FullName;
            File.WriteAllText(Path.Combine(sessions, "1.jsonl"), $"{{\"type\":\"user\",\"cwd\":{JsonSerializer.Serialize(target)}}}\n");
            File.WriteAllText(Path.Combine(sessions, "2.jsonl"), $"{{\"type\":\"user\",\"cwd\":{JsonSerializer.Serialize(collidingFolder)}}}\n");
            File.WriteAllText(Path.Combine(sessions, "3.jsonl"), ""); // just started, no cwd yet: counted

            var info = new ClaudeCodeSessionStore(projects).Lookup(target);

            Assert.NotNull(info);
            Assert.Equal(2, info.Count);
            Assert.NotNull(info.LastActivityUtc);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void LookupIsUnknownWhenTheStoreDoesNotExist()
    {
        string missing = Path.Combine(Path.GetTempPath(), "rondel-tests-" + Guid.NewGuid().ToString("N"));

        Assert.Null(new ClaudeCodeSessionStore(missing).Lookup(@"C:\anything"));
    }
}
