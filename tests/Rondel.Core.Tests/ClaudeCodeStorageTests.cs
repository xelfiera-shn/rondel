using System.Text.Json;
using Rondel.Core.Agents.ClaudeCode;

namespace Rondel.Core.Tests;

/// <summary>The Sessions window's view of Claude Code's folder: what a session is on disk, and what deleting it takes.</summary>
public class ClaudeCodeStorageTests
{
    const string Old = "11111111-1111-4111-8111-111111111111";
    const string Open = "22222222-2222-4222-8222-222222222222";
    const string Gone = "33333333-3333-4333-8333-333333333333";

    [Fact]
    public void ASessionIsItsTranscriptAndWhatIsKeptForIt()
    {
        var root = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            var (storage, project, temp) = MadeUp(root.FullName);

            var projects = storage.List();
            Assert.Equal(2, projects.Count);
            var work = projects.Single(p => p.DirectoryExists);
            Assert.Equal(project, work.Directory);
            Assert.Equal([Open, Old], work.Sessions.Select(s => s.Id)); // newest first
            var old = work.Sessions.Single(s => s.Id == Old);
            Assert.Equal("Fix the login page", old.Title);
            Assert.False(old.Running);
            Assert.True(work.Sessions.Single(s => s.Id == Open).Running); // a running Claude Code has it open
            // The transcript, and ten bytes each in the one set aside, subagents, file history, a debug log and both temporary folders.
            long expected = new FileInfo(Path.Combine(root.FullName, "claude", "projects", work.Key, Old + ".jsonl")).Length + 60;
            Assert.Equal(expected, old.Bytes);
            Assert.Equal(10, work.MemoryBytes);
            Assert.False(projects.Single(p => !p.DirectoryExists).DirectoryExists); // its folder was deleted

            // The open session and the memory stay; everything of the old one goes, and nothing else.
            var result = storage.Delete([new CleanupRequest(work.Key, [Old, Open], Memory: false)], recycle: null);
            Assert.Equal(1, result.Sessions);
            Assert.Equal(expected, result.Bytes);
            Assert.Equal([Open], result.Skipped);
            string folder = Path.Combine(root.FullName, "claude", "projects", work.Key);
            Assert.Equal([Open + ".jsonl", "memory"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order());
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "claude", "file-history", Old)));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root.FullName, "claude", "debug")));
            Assert.False(Directory.Exists(Path.Combine(temp, work.Key))); // left empty: removed
            Assert.False(Directory.Exists(Path.Combine(temp, ClaudeCodeStorage.HashedKey(work.Key))));
            Assert.True(File.Exists(Path.Combine(root.FullName, "claude", "settings.json")));

            // With its memory, a project whose last session goes leaves no folder behind.
            var gone = storage.List().Single(p => !p.DirectoryExists);
            storage.Delete([new CleanupRequest(gone.Key, [Gone], Memory: true)], recycle: null);
            Assert.DoesNotContain(storage.List(), p => p.Key == gone.Key);
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "claude", "projects", gone.Key)));
            Assert.Equal(7, storage.CleanupPeriodDays());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheRecycleBinGetsThePathsAndNothingIsDeletedBehindIt()
    {
        var root = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            var (storage, _, _) = MadeUp(root.FullName);
            var work = storage.List().Single(p => p.DirectoryExists);
            var recycled = new List<string>();

            var result = storage.Delete([new CleanupRequest(work.Key, [Old, "../../settings"], Memory: true)], recycled.AddRange);

            Assert.Equal(1, result.Sessions);
            Assert.Equal(1, result.Memories);
            Assert.Equal(8, recycled.Count); // the seven of the session and the memory folder; the made-up id reaches nothing
            Assert.All(recycled, path => Assert.True(File.Exists(path) || Directory.Exists(path))); // the bin moves them, not this
            Assert.Equal(ClaudeCodeStorage.DefaultCleanupDays, new ClaudeCodeStorage(Path.Combine(root.FullName, "none")).CleanupPeriodDays());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("C--Work-Demo")]
    [InlineData("c--work-demo")]
    public void NewerVersionsNameTheTemporaryFolderAfterAHashOfTheProject(string key)
    {
        Assert.Equal("1059d7f9bed0e949", ClaudeCodeStorage.HashedKey(key));
    }

    /// <summary>
    /// A Claude Code folder with a project that exists (an old session with all its files, and an open one) and one
    /// whose folder is gone, plus a temporary folder for the scratchpads.
    /// </summary>
    static (ClaudeCodeStorage Storage, string Project, string Temp) MadeUp(string root)
    {
        string config = Path.Combine(root, "claude");
        string temp = Path.Combine(root, "temp");
        string project = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        string key = ClaudeCodeSessionStore.Encode(project);
        string folder = Directory.CreateDirectory(Path.Combine(config, "projects", key)).FullName;
        void Ten(params string[] parts)
        {
            string path = Path.Combine(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "0123456789");
        }

        string cwd = JsonSerializer.Serialize(project);
        string line = $"{{\"type\":\"user\",\"cwd\":{cwd},\"message\":{{\"role\":\"user\",\"content\":\"Fix the login page\"}}}}";
        // The old session: its transcript, and ten bytes in each file kept for it.
        string transcript = Path.Combine(folder, Old + ".jsonl");
        File.WriteAllText(transcript, line + "\n");
        Ten(folder, Old + ".orphaned-1700000000-ab.jsonl");
        Ten(folder, Old, "subagents", "agent-1.jsonl");
        Ten(config, "file-history", Old, "a.txt");
        Ten(config, "debug", Old + ".txt");
        Ten(temp, key, Old, "scratchpad", "notes.md");
        Ten(temp, ClaudeCodeStorage.HashedKey(key), Old, "tasks", "1.txt"); // where newer versions keep it
        Ten(folder, "memory", "MEMORY.md");
        File.WriteAllText(Path.Combine(config, "settings.json"), "{ \"cleanupPeriodDays\": 7 }");
        File.SetLastWriteTimeUtc(transcript, DateTime.UtcNow.AddDays(-2));

        File.WriteAllText(Path.Combine(folder, Open + ".jsonl"), line);
        Directory.CreateDirectory(Path.Combine(config, "sessions"));
        File.WriteAllText(Path.Combine(config, "sessions", "1.json"),
            $"{{\"pid\":{Environment.ProcessId},\"sessionId\":\"{Open}\"}}");
        File.SetLastWriteTimeUtc(Path.Combine(folder, Open + ".jsonl"), DateTime.UtcNow.AddDays(-1));

        string goneKey = ClaudeCodeSessionStore.Encode(Path.Combine(root, "deleted"));
        string goneFolder = Directory.CreateDirectory(Path.Combine(config, "projects", goneKey)).FullName;
        File.WriteAllText(Path.Combine(goneFolder, Gone + ".jsonl"),
            $"{{\"type\":\"user\",\"cwd\":{JsonSerializer.Serialize(Path.Combine(root, "deleted"))}}}");
        File.SetLastWriteTimeUtc(Path.Combine(goneFolder, Gone + ".jsonl"), DateTime.UtcNow.AddDays(-3));
        Ten(goneFolder, "memory", "MEMORY.md");
        return (new ClaudeCodeStorage(config, temp), project, temp);
    }
}
