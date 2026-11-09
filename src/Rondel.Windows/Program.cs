using Rondel.Core.Activity;

namespace Rondel.Windows;

/// <summary>
/// The entry point. As the status line of a session it started (<see cref="StatusLineTap"/>), Rondel does that and
/// exits before anything of the app loads: Claude Code runs it after every reply, and the user's own status line
/// waits for it.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args is [StatusLineTap.Argument, ..])
            return StatusLineTap.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), UsageStore.Default);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
