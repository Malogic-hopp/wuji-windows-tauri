using System.Globalization;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.Client.Bridge.Generated;
using QuantifiedSelf.Windows.Core.Control;

namespace QuantifiedSelf.Windows.Client.Bridge;

internal static class BridgeAgentMapper
{
    public static AgentStatus ToStatus(AgentStatusSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new AgentStatus
        {
            ActualState = ToState(source.ActualState),
            IsRunning = source.IsRunning,
            IsHealthy = source.IsHealthy,
            IsStale = source.IsStale,
            LastHeartbeatUtc = FormatUtc(source.RuntimeState?.LastHeartbeatUtc),
            LastSampleUtc = FormatUtc(source.RuntimeState?.LastSampleUtc)
        };
    }

    public static CommandResult ToCommandResult(AgentCommandResult source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new CommandResult
        {
            Accepted = source.Accepted,
            Completed = source.Completed,
            ActualState = ToState(source.ActualState),
            UsedFallback = false,
            Message = ToSafeMessage(source.Accepted, source.Completed, source.ErrorCode),
            ErrorCode = ToSafeErrorCode(source.ErrorCode)
        };
    }

    public static CommandResult ToStartResult(AgentProcessInfo process, AgentStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(status);

        return new CommandResult
        {
            Accepted = process.IsRunning,
            Completed = process.IsRunning,
            ActualState = ToState(status.ActualState),
            UsedFallback = false,
            Message = process.IsRunning ? "Agent 已启动。" : "Agent 未能启动。",
            ErrorCode = process.IsRunning ? null : "agent_start_failed"
        };
    }

    public static CommandResult ToStopResult(AgentStopResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new CommandResult
        {
            Accepted = true,
            Completed = result.IsStopped,
            ActualState = result.IsStopped ? AgentState.Stopped : AgentState.Stopping,
            UsedFallback = result.UsedKillFallback,
            Message = result.IsStopped
                ? result.UsedKillFallback ? "Agent 已通过兜底方式停止。" : "Agent 已停止。"
                : "Agent 停止请求尚未完成。",
            ErrorCode = result.IsStopped ? null : "agent_stop_failed"
        };
    }

    private static AgentState ToState(AgentActualState state) => state switch
    {
        AgentActualState.NotRunning => AgentState.NotRunning,
        AgentActualState.Starting => AgentState.Starting,
        AgentActualState.Running => AgentState.Running,
        AgentActualState.Pausing => AgentState.Pausing,
        AgentActualState.Paused => AgentState.Paused,
        AgentActualState.Resuming => AgentState.Resuming,
        AgentActualState.Stopping => AgentState.Stopping,
        AgentActualState.Stopped => AgentState.Stopped,
        AgentActualState.Stale => AgentState.Stale,
        AgentActualState.Error => AgentState.Error,
        AgentActualState.Maintenance => AgentState.Maintenance,
        _ => AgentState.Error
    };

    private static string ToSafeMessage(bool accepted, bool completed, string? errorCode)
    {
        return ToSafeErrorCode(errorCode) switch
        {
            "ipc_timeout" => "Agent 命令已发送，但未在超时前确认结果。",
            "request_cancelled" => "Agent 命令已取消。",
            _ when !accepted => "Agent 拒绝了命令。",
            _ when completed => "Agent 命令已完成。",
            _ => "Agent 命令已提交。"
        };
    }

    private static string? ToSafeErrorCode(string? errorCode) => errorCode switch
    {
        null or "" => null,
        "IpcTimeout" => "ipc_timeout",
        "Cancelled" => "request_cancelled",
        _ => "agent_command_failed"
    };

    private static string? FormatUtc(DateTime? value)
    {
        if (value is null || value == DateTime.MinValue)
        {
            return null;
        }

        return value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
