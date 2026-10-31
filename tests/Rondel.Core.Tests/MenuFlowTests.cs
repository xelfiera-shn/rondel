using Rondel.Core.Context;
using Rondel.Core.Flow;
using Rondel.Core.Launch;
using Rondel.Core.Menus;
using Rondel.Core.Platform;
using Rondel.Core.Sessions;
using Rondel.Core.Settings;

namespace Rondel.Core.Tests;

/// <summary>The shared flow every platform relies on: what choosing a menu item starts, and where.</summary>
public class MenuFlowTests
{
    readonly FakeHost _host = new();
    readonly FakeLauncher _launcher = new();
    readonly MenuFlow _flow;

    public MenuFlowTests()
    {
        var settings = new AppSettings();
        settings.Normalize();
        _flow = new MenuFlow(_host, _launcher, new RulesOnly(), () => settings);
    }

    [Fact]
    public void ARecentProjectResumesItsLatestSessionInItsOwnFolder()
    {
        var model = _flow.Build(new TriggerContext(ContextKind.DesktopEmpty, Path.GetTempPath(), null, "test"))!;
        var recents = model.Items.Single(i => i.Action.Id == "recents");
        string alpha = Path.Combine(Path.GetTempPath(), "work", "alpha");
        var project = MenuBuilder.ProjectItems(recents, [new FolderActivity(alpha, DateTime.UtcNow, 1, "s1")], _ => [])[0];

        _flow.Invoke(model, recents); // the slice itself only opens its items
        Assert.Empty(_launcher.Plans);

        _flow.Invoke(model, project);
        var plan = Assert.Single(_launcher.Plans);
        Assert.Equal(alpha, plan.Directory);
        Assert.Equal(["--resume", "s1"], plan.Arguments);
        Assert.Equal(["close False"], _host.Calls);
    }

    [Fact]
    public void ANewProjectAsksForANameThenCreatesTheFolderAndStartsThere()
    {
        var parent = Directory.CreateTempSubdirectory("rondel-tests-");
        try
        {
            var model = _flow.Build(new TriggerContext(ContextKind.ExplorerEmpty, parent.FullName, null, "test"))!;

            _flow.Invoke(model, model.Items.Single(i => i.Action.Id == "new-project"));
            var input = Assert.IsType<InputSpec>(_host.Input);
            Assert.Equal(parent.FullName, input.ParentDir);
            Assert.Empty(_launcher.Plans);

            Assert.Null(input.Submit("demo", null));
            string created = Path.Combine(parent.FullName, "demo");
            Assert.True(Directory.Exists(created));
            Assert.Equal(created, Assert.Single(_launcher.Plans).Directory);
            Assert.NotNull(input.Submit("demo", null)); // exists now: shown inline, nothing started
            Assert.Single(_launcher.Plans);
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    sealed class FakeHost : IMenuHost
    {
        public List<string> Calls { get; } = [];

        public InputSpec? Input { get; private set; }

        public void CloseMenu(bool restoreFocus) => Calls.Add($"close {restoreFocus}");

        public void StartInput(InputSpec spec) => Input = spec;

        public void ShowWarning(string message) => Calls.Add($"warn {message}");
    }

    sealed class FakeLauncher : ITerminalLauncher
    {
        public List<LaunchPlan> Plans { get; } = [];

        public void Launch(LaunchPlan plan, AppSettings settings) => Plans.Add(plan);
    }

    sealed class RulesOnly : IFolderNameValidator
    {
        public string? Validate(string parent, string name) => FolderNameRules.Validate(parent, name, FolderNameRules.LongPathLimit);
    }
}
