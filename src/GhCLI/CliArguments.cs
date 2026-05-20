namespace GhCLI;

internal sealed class CliArguments
{
    public string Command { get; set; } = string.Empty;
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static CliArguments Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("A command is required.");
        }

        var parsed = new CliArguments();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(parsed.Command))
                {
                    parsed.Command = token.Trim();
                }

                continue;
            }

            var key = token[2..];
            string? value = null;

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[i + 1];
                i++;
            }
            else
            {
                value = "true";
            }

            parsed.Options[key] = value;
        }

        if (string.IsNullOrWhiteSpace(parsed.Command))
        {
            throw new ArgumentException("A command is required.");
        }

        return parsed;
    }
}
