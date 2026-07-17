import { invoke } from '@tauri-apps/api/core';
import type {
  ActivityOverviewResult,
  AgentStatus,
  ClientInitializeResult,
  CommandResult,
  SettingsFieldError,
  SettingsGetResult,
  SettingsSnapshot,
  SettingsUpdateParams,
  SettingsUpdateResult,
} from './contracts';

export type AgentCommand = 'start' | 'pause' | 'resume' | 'stop';

export interface CommandError {
  readonly code: string;
  readonly message: string;
  readonly retryable: boolean;
  readonly correlationId?: string;
  readonly fieldErrors?: ReadonlyArray<SettingsFieldError>;
}

export const commandWhitelist = {
  initialize: 'app_initialize',
  agentStatus: 'agent_get_status',
  agentStart: 'agent_start',
  agentPause: 'agent_pause',
  agentResume: 'agent_resume',
  agentStop: 'agent_stop',
  activityOverview: 'activity_get_overview',
  settingsGet: 'settings_get',
  settingsUpdate: 'settings_update',
  bridgeRetry: 'bridge_retry',
  setUnsavedChanges: 'app_set_unsaved_changes',
  windowShow: 'window_show',
  windowHide: 'window_hide',
  requestExit: 'app_request_exit',
  cancelClose: 'app_cancel_close',
} as const;

const agentCommands: Record<AgentCommand, string> = {
  start: commandWhitelist.agentStart,
  pause: commandWhitelist.agentPause,
  resume: commandWhitelist.agentResume,
  stop: commandWhitelist.agentStop,
};

export const bridgeClient = {
  initialize: () => invoke<ClientInitializeResult>(commandWhitelist.initialize),
  getAgentStatus: () => invoke<AgentStatus>(commandWhitelist.agentStatus),
  runAgentCommand: (command: AgentCommand) =>
    invoke<CommandResult>(agentCommands[command]),
  getActivityOverview: () =>
    invoke<ActivityOverviewResult>(commandWhitelist.activityOverview),
  getSettings: () => invoke<SettingsGetResult>(commandWhitelist.settingsGet),
  updateSettings: (settings: SettingsSnapshot) => {
    const request: SettingsUpdateParams = { settings };
    return invoke<SettingsUpdateResult>(commandWhitelist.settingsUpdate, { request });
  },
  retry: () => invoke<ClientInitializeResult>(commandWhitelist.bridgeRetry),
  setUnsavedChanges: (hasUnsavedChanges: boolean) =>
    invoke<null>(commandWhitelist.setUnsavedChanges, { hasUnsavedChanges }),
  showWindow: () => invoke<null>(commandWhitelist.windowShow),
  hideWindow: () => invoke<null>(commandWhitelist.windowHide),
  requestExit: () => invoke<null>(commandWhitelist.requestExit),
  cancelClose: () => invoke<null>(commandWhitelist.cancelClose),
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
