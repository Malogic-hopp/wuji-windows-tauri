namespace QuantifiedSelf.Windows.Infrastructure.Ipc;

public sealed class AgentIpcClientOptions
{
    public int ConnectTimeoutMilliseconds { get; set; } = 1000;
    public int RequestTimeoutMilliseconds { get; set; } = 5000;
    public int MaintenanceCommandTimeoutMilliseconds { get; set; } = 30000;
}
