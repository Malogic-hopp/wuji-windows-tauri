import { invoke } from '@tauri-apps/api/core';
import type {
  AgentStatusDto,
  HeatmapDto,
  SettingsDto,
  StatsHomeDto,
  StatsStatusDto,
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

/** Desktop 本地偏好（09 §9.4）：与 Agent effectivity Settings 分离，
 *  不参与 digest/CAS/LKG/数据库，无 expectedRevision。
 *  语义：启动吾迹时自动开始记录（ensure_running 后提交内部
 *  capture_ensure_recording：Stopped→开始 / Paused→恢复 / Running→幂等）。 */
export interface DesktopPrefsDto {
  autoStartRecordingWhenAppStarts: boolean;
}

export interface DesktopPrefsPatchInput {
  autoStartRecordingWhenAppStarts: boolean;
}

/** 自动开始记录编排状态（09 §9.3，Host 侧 AutoStartSnapshot）：
 *  idle=未启用 / starting=正在开始记录 / recording=成功 / failed=失败（含错误）。 */
export interface AutoStartDto {
  state: 'idle' | 'starting' | 'recording' | 'failed';
  error: SafeError | null;
}

export const bridgeClient = {
  agentProcessStop: () => invoke<AgentStatusDto>('agent_process_stop'),
  agentGetStatus: () => invoke<AgentStatusDto>('agent_get_status'),
  captureStart: () => invoke<AgentStatusDto>('capture_start'),
  capturePause: () => invoke<AgentStatusDto>('capture_pause'),
  captureResume: () => invoke<AgentStatusDto>('capture_resume'),
  activityGetToday: () => invoke<TodayDto>('activity_get_today'),
  activityGetHeatmap: (days?: number, weekOffset?: number) =>
    invoke<HeatmapDto>('activity_get_heatmap', {
      days: days ?? null,
      weekOffset: weekOffset === 0 ? null : (weekOffset ?? null),
    }),
  activityGetTimeline: (localDate: string, cursor?: string, limit?: number) =>
    invoke<TimelinePageDto>('activity_get_timeline', {
      localDate,
      cursor: cursor ?? null,
      limit: limit ?? null,
    }),
  /** 统计主页全量（10 设计 §5.4）：进入/跨日期/切换范围时调用（阶段四仅签名）。 */
  statsGetHome: (days?: number) =>
    invoke<StatsHomeDto>('stats_get_home', { days: days ?? null }),
  /** 统计主页轻量轮询（阶段四仅签名）：状态卡/本周进度/今日趋势点随顶栏同拍更新。 */
  statsGetStatus: () => invoke<StatsStatusDto>('stats_get_status'),
  settingsGet: () => invoke<SettingsDto>('settings_get'),
  settingsUpdate: (patch: SettingsPatchInput) =>
    invoke<SettingsDto>('settings_update', { patch }),
  settingsResyncLoginStartup: () =>
    invoke<SettingsDto>('settings_resync_login_startup'),
  desktopPrefsGet: () => invoke<DesktopPrefsDto>('desktop_prefs_get'),
  desktopPrefsUpdate: (patch: DesktopPrefsPatchInput) =>
    invoke<DesktopPrefsDto>('desktop_prefs_update', { patch }),
  autoStartStatus: () => invoke<AutoStartDto>('auto_start_status'),
  diagnosticsGetSummary: () => invoke<DiagnosticsDto>('diagnostics_get_summary'),
};
