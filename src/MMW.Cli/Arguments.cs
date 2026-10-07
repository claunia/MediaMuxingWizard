namespace MMW.Cli;

/// <summary>Minimal "--name value" / "--flag" / positional argument parser.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    private static readonly HashSet<string> s_flags =
    [
        "json", "replace", "remove-all", "clear", "organize-groups", "fix-fallbacks", "clear-names", "prettify-audio-names", "optimize", "apply",
        "all", "dry-run", "drop-unsupported",
    ];

    public static Arguments Parse(IReadOnlyList<string> args)
    {
        var result = new Arguments();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (s_flags.Contains(name))
            {
                result._flags.Add(name);
                continue;
            }

            // An option with no value after it acts as a flag (e.g. "nfo file --export").
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result._flags.Add(name);
                continue;
            }

            if (!result._options.TryGetValue(name, out var list))
                result._options[name] = list = [];

            // --add takes every following non-option argument.
            do
            {
                list.Add(args[++i]);
            }
            while (name == "add" && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal));
        }

        return result;
    }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Value(string name) => _options.TryGetValue(name, out var v) ? v[^1] : null;

    public IReadOnlyList<string> Values(string name) => _options.TryGetValue(name, out var v) ? v : [];
}
