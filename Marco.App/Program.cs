using System.Threading;
using Marco.App.Cli;
using Marco.Core.Cli;

namespace Marco.App;

/// <summary>
/// Process entry point. A `scan` first argument runs the headless CLI path — attaching to the parent console,
/// never spinning up WPF, and never touching the update pipeline. Anything else launches the WPF app exactly as
/// before. Keep this small: forgetting App.InitializeComponent() would silently break every StaticResource.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "scan", StringComparison.OrdinalIgnoreCase))
            return RunCli(args.Skip(1).ToArray());

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int RunCli(string[] scanArgs)
    {
        ConsoleAttach.TryAttachToParent();

        var parsed = CliArgumentParser.Parse(scanArgs);
        if (parsed is CliParseError error)
        {
            Console.Error.WriteLine(error.Message);
            return (int)CliExitCode.Usage;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return CliScanCommand.RunAsync((CliOptions)parsed, cts.Token).GetAwaiter().GetResult();
    }
}
