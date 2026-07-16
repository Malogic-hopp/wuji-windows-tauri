using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Infrastructure.Events;

namespace QuantifiedSelf.Windows.Agent.Events;

public sealed class AgentEventWriter
{
    private readonly AgentEventRepository _eventRepository;
    private readonly AgentEventJournal _eventJournal;
    private readonly object _gate = new();

    private string? _lastEventWriteError;
    private string? _lastJournalWriteError;
    private DateTime? _lastEventWriteErrorUtc;
    private DateTime? _lastJournalWriteErrorUtc;
    private int _eventWriteErrorCount;
    private int _journalWriteErrorCount;

    public AgentEventWriter(AgentEventRepository eventRepository, AgentEventJournal eventJournal)
    {
        _eventRepository = eventRepository;
        _eventJournal = eventJournal;
    }

    public string? LastEventWriteError
    {
        get
        {
            lock (_gate)
            {
                return _lastEventWriteError;
            }
        }
    }

    public string? LastJournalWriteError
    {
        get
        {
            lock (_gate)
            {
                return _lastJournalWriteError;
            }
        }
    }

    public DateTime? LastEventWriteErrorUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastEventWriteErrorUtc;
            }
        }
    }

    public DateTime? LastJournalWriteErrorUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastJournalWriteErrorUtc;
            }
        }
    }

    public int EventWriteErrorCount
    {
        get
        {
            lock (_gate)
            {
                return _eventWriteErrorCount;
            }
        }
    }

    public int JournalWriteErrorCount
    {
        get
        {
            lock (_gate)
            {
                return _journalWriteErrorCount;
            }
        }
    }

    public string GetJournalPath(DateTime utcNow)
    {
        return _eventJournal.GetJournalPath(utcNow);
    }

    public async Task WriteAsync(
        AgentEvent agentEvent,
        WindowsAgentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        try
        {
            await _eventRepository.InsertAsync(agentEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordEventWriteError(ex);
        }

        if (options?.EnableAgentEventJournal == false)
        {
            return;
        }

        try
        {
            await _eventJournal.AppendAsync(agentEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordJournalWriteError(ex);
        }
    }

    private void RecordEventWriteError(Exception exception)
    {
        lock (_gate)
        {
            _eventWriteErrorCount++;
            _lastEventWriteErrorUtc = DateTime.UtcNow;
            _lastEventWriteError = FormatError(exception);
        }
    }

    private void RecordJournalWriteError(Exception exception)
    {
        lock (_gate)
        {
            _journalWriteErrorCount++;
            _lastJournalWriteErrorUtc = DateTime.UtcNow;
            _lastJournalWriteError = FormatError(exception);
        }
    }

    private static string FormatError(Exception exception)
    {
        var message = DiagnosticMessageSanitizer.CreateSafeText(exception.Message, 200);
        return string.IsNullOrWhiteSpace(message)
            ? exception.GetType().Name
            : $"{exception.GetType().Name}: {message}";
    }
}
