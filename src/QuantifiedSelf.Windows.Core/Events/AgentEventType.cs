namespace QuantifiedSelf.Windows.Core.Events;

public enum AgentEventType
{
    AgentStarted,
    AgentStopped,
    AgentPaused,
    AgentResumed,
    CommandDetected,
    CommandAccepted,
    CommandCompleted,
    CommandFailed,
    CommandInvalidJson,
    ConfigReloaded,
    PrivacyFiltered,
    CaptureFailed,
    SessionStarted,
    SessionClosed
}
