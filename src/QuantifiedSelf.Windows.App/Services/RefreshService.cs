using QuantifiedSelf.Windows.App.Models;
using QuantifiedSelf.Windows.Core.Events;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class RefreshService
{
    private readonly AgentStatusService _statusService;
    private readonly AgentProcessService _processService;
    private readonly RefreshOptions _options;
    private readonly RefreshHealthSnapshot _health = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long _refreshSequence;

    public RefreshService(
        AgentStatusService statusService,
        AgentProcessService processService,
        RefreshOptions? options = null)
    {
        _statusService = statusService;
        _processService = processService;
        _options = options ?? new RefreshOptions();
    }

    public RefreshHealthSnapshot Health => _health;

    public async Task<RefreshResult> RefreshAsync(string currentPage, CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            _health.RecordSkipped();
            return new RefreshResult
            {
                RefreshSequence = Interlocked.Read(ref _refreshSequence),
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                Status = new AgentStatusSnapshot(),
                Health = _health,
                CurrentPage = currentPage,
                StatusSource = "Unknown",
                PageRefreshSkipped = true
            };
        }

        try
        {
            _health.IsRefreshing = true;
            var startedAt = DateTime.UtcNow;
            var sequence = Interlocked.Increment(ref _refreshSequence);

            var status = await _statusService.GetStatusAsync(cancellationToken);
            var processInfo = await _processService.GetAgentProcessInfoAsync(cancellationToken);

            _health.RecordSuccess();

            return new RefreshResult
            {
                RefreshSequence = sequence,
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTime.UtcNow,
                Status = status,
                ProcessInfo = processInfo,
                Health = _health,
                CurrentPage = currentPage,
                StatusSource = "Unknown",
                PageDataRefreshed = false
            };
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            if (string.IsNullOrWhiteSpace(safeMessage))
            {
                safeMessage = "Refresh failed.";
            }

            _health.RecordError(safeMessage);

            return new RefreshResult
            {
                RefreshSequence = Interlocked.Read(ref _refreshSequence),
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                Status = new AgentStatusSnapshot(),
                Health = _health,
                CurrentPage = currentPage,
                StatusSource = "Unknown"
            };
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
