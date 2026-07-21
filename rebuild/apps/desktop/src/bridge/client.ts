import { invoke } from '@tauri-apps/api/core';
import type {
  AgentStatusDto,
  SettingsDto,
  TimelinePageDto,
  TodayDto,
} from '../types/wuji-core';

/** 与 Rust SafeError 对应的边界错误（09 §8.2）。 */
export interface SafeError {
  code: string;
  message: string;
}

export function toSafeError(cause: unknown): SafeError {
  if (typeof cause === 'object' && cause !== null && 'code' in cause) {
    const error = cause as { code: unknown; message?: unknown };
    return {
      code: String(error.code),
      message: typeof error.message === 'string' ? error.message : '操作失败',
    };
  }
  return { code: 'INTERNAL_SAFE_ERROR', message: '操作失败，请稍后重试' };
}

/** Diagnostics 摘要（desktop DiagnosticsDto）。 */
export interface DiagnosticsDto {
  status: AgentStatusDto | null;
  databaseReachable: boolean;
  settingsPersisted: boolean;
  appliedRevision: string;
  reportingTimeZoneId: string | null;
  dataRootMasked: string;
  agentExeMasked: string;
}

export interface SettingsPatchInput {
  expectedRevision: string;
  samplingIntervalSeconds: number;
  idleThresholdSeconds: number;
  workBreakIdleSeconds: number;
  excludedProcessNames: string[];
  startCaptureOnLogin: boolean;
}

export const bridgeClient = {
  agentProcessEnsureRunning: () =>
    invoke<AgentStatusDto>('agent_process_ensure_running'),
  agentGetStatus: () => invoke<AgentStatusDto>('agent_get_status'),
  captureStart: () => invoke<AgentStatusDto>('capture_start'),
  capturePause: () => invoke<AgentStatusDto>('capture_pause'),
  captureResume: () => invoke<AgentStatusDto>('capture_resume'),
  captureStop: () => invoke<AgentStatusDto>('capture_stop'),
  activityGetToday: () => invoke<TodayDto>('activity_get_today'),
  activityGetTimeline: (localDate: string, cursor?: string, limit?: number) =>
    invoke<TimelinePageDto>('activity_get_timeline', {
      localDate,
      cursor: cursor ?? null,
      limit: limit ?? null,
    }),
  settingsGet: () => invoke<SettingsDto>('settings_get'),
  settingsUpdate: (patch: SettingsPatchInput) =>
    invoke<SettingsDto>('settings_update', { patch }),
  settingsResyncLoginStartup: () =>
    invoke<SettingsDto>('settings_resync_login_startup'),
  diagnosticsGetSummary: () => invoke<DiagnosticsDto>('diagnostics_get_summary'),
};
