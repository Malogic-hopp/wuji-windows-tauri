using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.Core.Events;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class RefreshService
{
    private readonly AgentStatusService _statusService;
    private readonly AgentProcessService _processService;
    private readonly RefreshOptions _options;
    private readonly RefreshHealthSnapshot _health = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _statusGate = new(1, 1);
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

    /// <summary>
    /// Status-only refresh — fetches Agent status/process but does NOT refresh page data.
    /// Used by the 2-second status polling timer.
    /// </summary>
    public async Task<RefreshResult> RefreshStatusAsync(string currentPage, CancellationToken cancellationToken = default)
    {
        // Status polling uses a dedicated gate independent of full refresh,
        // so 2s status polls never block page-data refreshes.
        if (!await _statusGate.WaitAsync(0, cancellationToken))
        {
            _health.RecordStatusSkipped();
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
            _health.IsStatusRefreshing = true;
            var startedAt = DateTime.UtcNow;
            var sequence = Interlocked.Increment(ref _refreshSequence);

            var status = await _statusService.GetStatusAsync(cancellationToken);
            var processInfo = await _processService.GetAgentProcessInfoAsync(cancellationToken);

            _health.RecordStatusSuccess();

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal: superseded by a newer status poll — not an error
            _health.IsStatusRefreshing = false;
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
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            if (string.IsNullOrWhiteSpace(safeMessage))
            {
                safeMessage = "Refresh failed.";
            }

            _health.RecordStatusError(safeMessage);

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
            _statusGate.Release();
        }
    }

    public async Task<RefreshResult> RefreshAsync(string currentPage, CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            _health.RecordStatusSkipped();
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
            _health.IsStatusRefreshing = true;
            var startedAt = DateTime.UtcNow;
            var sequence = Interlocked.Increment(ref _refreshSequence);

            var status = await _statusService.GetStatusAsync(cancellationToken);
            var processInfo = await _processService.GetAgentProcessInfoAsync(cancellationToken);

            _health.RecordStatusSuccess();

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

            _health.RecordStatusError(safeMessage);

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
