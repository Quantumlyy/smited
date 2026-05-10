using Smited.Daemon.Configuration;
using Spectre.Console;

namespace Smited.Daemon.Diagnostics;

/// <summary>
/// Renders the startup banner shown after <c>ApplicationStarted</c>. The
/// panic row is coloured red so the operator can't miss the panic port
/// on every startup.
/// </summary>
internal static class StartupBanner
{
    public static void Render(
        SmitedOptions options,
        int backendCount,
        int sensationCount,
        string? historyDbPath)
    {
        var grid = new Grid().AddColumn().AddColumn();

        grid.AddRow(
            "[aquamarine1]Listening[/]",
            $"[gold1]gRPC {options.BindAddress}:{options.GrpcPort} (h2c{(options.EnableReflection ? ", reflection on" : "")})[/]");

        grid.AddRow(
            "[aquamarine1]Panic[/]",
            $"[red1]POST http://{options.BindAddress}:{options.PanicPort}/panic[/]");

        grid.AddRow("[aquamarine1]Backends[/]", $"[gold1]{backendCount} registered[/]");
        grid.AddRow("[aquamarine1]Sensations[/]", $"[gold1]{sensationCount} loaded[/]");
        grid.AddRow(
            "[aquamarine1]History[/]",
            options.History.Enabled && historyDbPath is not null
                ? $"[gold1]{Markup.Escape(historyDbPath)}[/]"
                : "[grey]disabled[/]");

        AnsiConsole.Write(
            new Panel(grid)
                .Header("[bold mediumpurple1]smited[/]")
                .Border(BoxBorder.Rounded));
    }
}
