namespace TechnicsSim.Cli;

/// <summary>
/// A deliberately small argument parser. The CLI is a permanent audit tool, not a demo, so
/// it should not carry a third-party dependency whose API churn can break the build.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string command, IReadOnlyList<string> positionals)
    {
        Command = command;
        Positionals = positionals;
    }

    public string Command { get; }

    public IReadOnlyList<string> Positionals { get; }

    public static CommandLine Parse(string[] args)
    {
        var command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "help";
        var positionals = new List<string>();
        var line = new CommandLine(command, positionals);

        for (var i = command == "help" ? 0 : 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                line._options[name[..equals]] = name[(equals + 1)..];
                continue;
            }

            // A flag is followed by its value unless the next token is another option.
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            line._options[name] = hasValue ? args[++i] : null;
        }

        return line;
    }

    /// <summary>Returns an option's value, or null when the option was absent.</summary>
    public string? Option(string name) => _options.GetValueOrDefault(name);

    /// <summary>True when a valueless flag such as <c>--verbose</c> was present.</summary>
    public bool Flag(string name) => _options.ContainsKey(name);

    public string RequirePositional(int index, string description) =>
        index < Positionals.Count
            ? Positionals[index]
            : throw new CommandLineException($"Missing required argument: {description}.");
}

public sealed class CommandLineException(string message) : Exception(message);
