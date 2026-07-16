import { invoke } from '@tauri-apps/api/core';
import type { AgentStatus, ClientInitializeResult, CommandResult } from './contracts';

export type AgentCommand = 'start' | 'pause' | 'resume' | 'stop';

export interface CommandError {
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
  readonly correlationId?: string;
}

const agentCommands: Record<AgentCommand, string> = {
  start: 'agent_start',
  pause: 'agent_pause',
  resume: 'agent_resume',
  stop: 'agent_stop',
};

export const bridgeClient = {
  initialize: () => invoke<ClientInitializeResult>('app_initialize'),
  getAgentStatus: () => invoke<AgentStatus>('agent_get_status'),
  runAgentCommand: (command: AgentCommand) =>
    invoke<CommandResult>(agentCommands[command]),
  retry: () => invoke<ClientInitializeResult>('bridge_retry'),
};

export function toCommandError(error: unknown): CommandError {
  if (isCommandError(error)) {
    return error;
  }

  return {
    code: 'bridge_unavailable',
    message: '无法连接本地服务，请稍后重试。',
    retryable: true,
  };
}

function isCommandError(value: unknown): value is CommandError {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const candidate = value as Partial<CommandError>;
  return typeof candidate.code === 'string'
    && typeof candidate.message === 'string'
    && typeof candidate.retryable === 'boolean';
}
