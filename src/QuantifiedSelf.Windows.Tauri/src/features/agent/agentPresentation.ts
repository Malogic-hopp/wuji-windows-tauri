import type { AgentState } from '../../bridge/contracts';

export interface AgentPresentation {
  readonly label: string;
  readonly tone: 'neutral' | 'positive' | 'warning' | 'danger';
  readonly availableCommands: ReadonlySet<'start' | 'pause' | 'resume' | 'stop'>;
}

const startOnly = new Set(['start'] as const);
const pauseStop = new Set(['pause', 'stop'] as const);
const resumeStop = new Set(['resume', 'stop'] as const);
const stopOnly = new Set(['stop'] as const);
const noCommands = new Set<never>();

export function presentAgentState(state: AgentState): AgentPresentation {
  switch (state) {
    case 'running':
      return { label: '正在记录', tone: 'positive', availableCommands: pauseStop };
    case 'paused':
      return { label: '已暂停', tone: 'warning', availableCommands: resumeStop };
    case 'starting':
      return { label: '正在启动', tone: 'neutral', availableCommands: noCommands };
    case 'pausing':
      return { label: '正在暂停', tone: 'neutral', availableCommands: noCommands };
    case 'resuming':
      return { label: '正在恢复', tone: 'neutral', availableCommands: noCommands };
    case 'stopping':
      return { label: '正在停止', tone: 'neutral', availableCommands: noCommands };
    case 'stale':
      return { label: '状态待确认', tone: 'warning', availableCommands: stopOnly };
    case 'error':
      return { label: '服务异常', tone: 'danger', availableCommands: stopOnly };
    case 'maintenance':
      return { label: '维护中', tone: 'warning', availableCommands: noCommands };
    case 'not_running':
    case 'stopped':
      return { label: '未运行', tone: 'neutral', availableCommands: startOnly };
  }
}
