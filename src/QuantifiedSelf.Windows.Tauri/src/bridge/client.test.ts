import { beforeEach, describe, expect, it, vi } from 'vitest';
import { invoke } from '@tauri-apps/api/core';
import { bridgeClient, commandWhitelist } from './client';

vi.mock('@tauri-apps/api/core', () => ({
  invoke: vi.fn(),
}));

describe('bridgeClient semantic commands', () => {
  beforeEach(() => vi.clearAllMocks());

  it('只通过固定白名单 command 请求 Overview', async () => {
    vi.mocked(invoke).mockResolvedValue({
      summary: {
        dateUtc: '2026-07-16',
        totalDurationSeconds: 60,
        activeDurationSeconds: 40,
        idleDurationSeconds: 20,
        unknownDurationSeconds: 0,
        sessionCount: 1,
      },
      topApps: [],
      recentSessions: [],
    });

    await bridgeClient.getActivityOverview();

    expect(commandWhitelist.activityOverview).toBe('activity_get_overview');
    expect(invoke).toHaveBeenCalledOnce();
    expect(invoke).toHaveBeenCalledWith('activity_get_overview');
  });

  it('React 白名单只包含当前语义 command', () => {
    expect(Object.values(commandWhitelist)).toEqual([
      'app_initialize',
      'agent_get_status',
      'agent_start',
      'agent_pause',
      'agent_resume',
      'agent_stop',
      'activity_get_overview',
      'settings_get',
      'settings_update',
      'bridge_retry',
    ]);
  });

  it('通过固定 command 读取并原样提交生成的 Settings DTO', async () => {
    const settings = {
      appSettings: {
        theme: 'dark',
        refreshIntervalSeconds: 30,
        autoStartAgentWhenAppStarts: true,
      },
      agentOptions: {
        samplingIntervalSeconds: 3,
        idleThresholdSeconds: 300,
        heartbeatIntervalSeconds: 5,
        staleThresholdSeconds: 30,
        retentionDays: 90,
        enableJsonlJournal: false,
        enableAgentEventJournal: true,
        enableSessionMerge: true,
        maskWindowTitles: true,
      },
    } as const;
    vi.mocked(invoke).mockResolvedValue({ settings });

    await bridgeClient.getSettings();
    await bridgeClient.updateSettings(settings);

    expect(commandWhitelist.settingsGet).toBe('settings_get');
    expect(commandWhitelist.settingsUpdate).toBe('settings_update');
    expect(invoke).toHaveBeenNthCalledWith(1, 'settings_get');
    expect(invoke).toHaveBeenNthCalledWith(2, 'settings_update', {
      request: { settings },
    });
  });
});
