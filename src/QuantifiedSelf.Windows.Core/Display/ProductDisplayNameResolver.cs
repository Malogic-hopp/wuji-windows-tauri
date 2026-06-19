namespace QuantifiedSelf.Windows.Core.Display;

public static class ProductDisplayNameResolver
{
    private const string AppProcessName = "QuantifiedSelf.Windows.App";
    private const string AgentProcessName = "QuantifiedSelf.Windows.Agent";

    public static string Resolve(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        var normalizedProcessName = processName.Trim();
        return normalizedProcessName switch
        {
            AppProcessName => "WUJI",
            AgentProcessName => "WUJI Agent",
            _ => normalizedProcessName
        };
    }
}
