using TechnicsSim.Cli;
using TechnicsSim.Cli.Commands;

try
{
    var commandLine = CommandLine.Parse(args);

    if (commandLine.Command is "help" or "--help" or "-h")
    {
        PrintUsage();
        return 0;
    }

    using var workspace = Workspace.Open(commandLine);

    return commandLine.Command switch
    {
        "library-info" => LibraryInfoCommand.Run(workspace),
        "inspect-model" => InspectModelCommand.Run(workspace, commandLine),
        "coverage" => CoverageCommand.Run(workspace, commandLine),
        "mesh-stats" => MeshStatsCommand.Run(workspace, commandLine),
        _ => UnknownCommand(commandLine.Command),
    };
}
catch (CommandLineException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"error: file not found: {ex.FileName ?? ex.Message}");
    return 1;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"error: unknown command '{command}'.");
    Console.Error.WriteLine();
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        technicssim - LDraw model audit and mechanism reconstruction tools

        Usage:
          technicssim library-info
              Show the official LDraw library and LDCad shadow library in use,
              with the revisions every report depends on.

          technicssim inspect-model <model.mpd> [--root <section>] [--top <n>]
              Summarize sections, the three instance counts, and resolution failures.

          technicssim coverage <model.mpd> [--json <report.json>]
              Report logical part coverage and shadow-feature availability.

          technicssim mesh-stats <model.mpd> [--no-edges]
              Build the full render scene headlessly and report batching,
              instancing, triangle counts, and timings.

        Common options:
          --ldraw <path>    Official library: a directory, complete.zip, or LeoCAD library.bin.
                            Defaults to TECHNICSSIM_LDRAW_PATH, then Library/, then LeoCAD.
          --shadow <path>   LDCad shadow library checkout. Defaults to Library/LDCadShadowLibrary.

        Exit codes:
          0  success
          1  configuration or usage error
          2  the model has unresolved references
        """);
}
