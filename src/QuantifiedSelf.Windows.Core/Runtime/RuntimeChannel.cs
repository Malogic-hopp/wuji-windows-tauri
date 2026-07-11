namespace QuantifiedSelf.Windows.Core.Runtime;

public sealed class RuntimeChannel
{
    public const string DefaultName = "prod";
    public const string DevelopmentName = "dev";

    public string Name { get; }

    public bool IsDefault => string.Equals(Name, DefaultName, StringComparison.OrdinalIgnoreCase);

    public string ProductDisplayName => IsDefault ? "WUJI" : $"WUJI {DisplaySuffix}";

    public string DataRootProductFolder => IsDefault ? "WUJI" : $"WUJI-{DisplaySuffix}";

    public string StartupRegistryValueName => IsDefault ? "WUJI" : $"WUJI {DisplaySuffix}";

    public string? PipeQualifier => IsDefault ? null : Name;

    public string? AgentLaunchArguments => IsDefault ? null : $"--channel {Name}";

    private string DisplaySuffix => Name.Equals(DevelopmentName, StringComparison.OrdinalIgnoreCase)
        ? "Dev"
        : char.ToUpperInvariant(Name[0]) + Name[1..];

    private RuntimeChannel(string name)
    {
        Name = name;
    }

    public static RuntimeChannel Default { get; } = new(DefaultName);

    public static RuntimeChannel Development { get; } = new(DevelopmentName);

    public static RuntimeChannel Parse(string? value)
    {
        var normalized = Normalize(value);
        return normalized == DefaultName
            ? Default
            : normalized == DevelopmentName
                ? Development
                : new RuntimeChannel(normalized);
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultName;
        }

        var trimmed = value.Trim().TrimStart('-').ToLowerInvariant();
        if (trimmed is "production" or "stable")
        {
            return DefaultName;
        }

        if (trimmed is "development" or "preview")
        {
            return DevelopmentName;
        }

        var chars = trimmed
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray();

        return chars.Length == 0
            ? DefaultName
            : new string(chars);
    }
}
