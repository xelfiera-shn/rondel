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
    public void RecentListsNewestFirstWithTheLatestTitleOrTheFirstPrompt()
    {
        var root = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            string target = Path.Combine(root.FullName, "work");
            string projects = Path.Combine(root.FullName, "projects");
            string sessions = Directory.CreateDirectory(Path.Combine(projects, ClaudeCodeSessionStore.Encode(target))).FullName;
            string cwd = JsonSerializer.Serialize(target);
            string older = Path.Combine(sessions, "older.jsonl");
            File.WriteAllLines(older,
            [
                $"{{\"type\":\"user\",\"cwd\":{cwd},\"isMeta\":true,\"message\":{{\"role\":\"user\",\"content\":\"Caveat: meta\"}}}}",
                $"{{\"type\":\"user\",\"cwd\":{cwd},\"message\":{{\"role\":\"user\",\"content\":\"<command-name>/clear</command-name>\"}}}}",
                $"{{\"type\":\"user\",\"cwd\":{cwd},\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":\"  Fix the\\n login   page \"}}]}}}}",
            ]);
            string newer = Path.Combine(sessions, "newer.jsonl");
            File.WriteAllLines(newer,
            [
                $"{{\"type\":\"user\",\"cwd\":{cwd},\"message\":{{\"role\":\"user\",\"content\":\"first prompt\"}}}}",
                "{\"type\":\"ai-title\",\"aiTitle\":\"Early title\"}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"\\\"type\\\":\\\"user\\\"\"}]}}",
                "{\"type\":\"ai-title\",\"aiTitle\":\"Rondel UI redesign\"}",
            ]);
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-3));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(-5));

            var recent = new ClaudeCodeSessionStore(projects).Recent(target, 5);

            Assert.Equal(["newer", "older"], recent.Select(s => s.Id));
            Assert.Equal(["Rondel UI redesign", "Fix the login page"], recent.Select(s => s.Title));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void RecentFoldersListsTheNewestProjectsAndSkipsIgnoredAndDeletedFolders()
    {
        var root = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            string projects = Path.Combine(root.FullName, "projects");
            string ignored = Directory.CreateDirectory(Path.Combine(root.FullName, "scratch")).FullName;
            string alpha = Directory.CreateDirectory(Path.Combine(root.FullName, "work", "alpha")).FullName;
            string beta = Directory.CreateDirectory(Path.Combine(root.FullName, "work", "beta")).FullName;
            string tool = Directory.CreateDirectory(Path.Combine(ignored, "tool-run")).FullName;
            string deleted = Path.Combine(root.FullName, "work", "deleted");

            void Session(string cwd, string id, TimeSpan age)
            {
                string folder = Directory.CreateDirectory(Path.Combine(projects, ClaudeCodeSessionStore.Encode(cwd))).FullName;
                string file = Path.Combine(folder, id + ".jsonl");
                File.WriteAllText(file, $"{{\"type\":\"user\",\"cwd\":{JsonSerializer.Serialize(cwd)}}}\n");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow - age);
            }
            Session(alpha, "alpha-old", TimeSpan.FromDays(2));
            Session(alpha, "alpha-new", TimeSpan.FromHours(1));
            Session(beta, "beta-only", TimeSpan.FromHours(2));
            Session(tool, "tool", TimeSpan.FromMinutes(5));      // under an ignored root
            Session(deleted, "gone", TimeSpan.FromMinutes(10));  // folder no longer exists

            var recent = new ClaudeCodeSessionStore(projects, ignoredRoots: [ignored]).RecentFolders(5);

            Assert.Equal([alpha, beta], recent.Select(f => f.Directory));
            Assert.Equal([2, 1], recent.Select(f => f.SessionCount));
            Assert.Equal("alpha-new", recent[0].LatestSessionId);
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
