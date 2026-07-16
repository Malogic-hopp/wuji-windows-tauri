import {
  PauseIcon,
  PlayIcon,
  SpinnerGapIcon,
  StopIcon,
} from '@phosphor-icons/react';
import type { AgentCommand } from '../../bridge/client';
import type { AgentStatus } from '../../bridge/contracts';
import { presentAgentState } from './agentPresentation';

interface Props {
  readonly status?: AgentStatus;
  readonly busy: boolean;
  readonly disconnected: boolean;
  readonly commandError?: string;
  readonly onCommand: (command: AgentCommand) => void;
  readonly onRetry: () => void;
}

const commands = [
  { id: 'start', label: '启动', icon: PlayIcon },
  { id: 'pause', label: '暂停', icon: PauseIcon },
  { id: 'resume', label: '继续', icon: PlayIcon },
  { id: 'stop', label: '停止', icon: StopIcon },
] as const;

export function AgentCommandBar({
  status,
  busy,
  disconnected,
  commandError,
  onCommand,
  onRetry,
}: Props) {
  if (disconnected) {
    return (
      <section className="agent-bar agent-bar--error" aria-label="后台记录服务" role="alert">
        <div>
          <span className="status-dot status-dot--danger" aria-hidden="true" />
          <strong>连接已断开</strong>
          <span className="agent-bar__detail">Agent 会保持原状态运行</span>
        </div>
        <button type="button" className="button button--secondary" onClick={onRetry}>重新连接</button>
      </section>
    );
  }

  if (!status) {
    return (
      <section className="agent-bar" aria-label="后台记录服务" aria-live="polite">
        <SpinnerGapIcon className="spin" size={18} aria-hidden="true" />
        <span>正在连接本地服务…</span>
      </section>
    );
  }

  const presentation = presentAgentState(status.actualState);

  return (
    <section className="agent-bar" aria-label="后台记录服务">
      <div className="agent-bar__status" aria-live="polite">
        <span className={`status-dot status-dot--${presentation.tone}`} aria-hidden="true" />
        <strong>{presentation.label}</strong>
        {commandError ? (
          <span className="agent-bar__command-error" role="alert">{commandError}</span>
        ) : (
          <span className="agent-bar__detail">
            {status.isHealthy ? '服务响应正常' : '等待健康状态'}
          </span>
        )}
      </div>
      <div className="agent-bar__actions" aria-label="Agent 操作">
        {commands.map(({ id, label, icon: Icon }) => (
          <button
            key={id}
            type="button"
            className={id === 'stop' ? 'button button--quiet-danger' : 'button button--secondary'}
            disabled={busy || !presentation.availableCommands.has(id)}
            onClick={() => onCommand(id)}
          >
            <Icon size={17} weight="bold" aria-hidden="true" />
            {label}
          </button>
        ))}
      </div>
    </section>
  );
}
