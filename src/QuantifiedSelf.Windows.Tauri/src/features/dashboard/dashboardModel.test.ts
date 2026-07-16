import { describe, expect, it } from 'vitest';
import type { ActivityOverviewResult } from '../../bridge/contracts';
import {
  getOverviewRefreshInterval,
  isOverviewEmpty,
  overviewHiddenRefreshInterval,
  overviewVisibleRefreshInterval,
} from './dashboardModel';

const emptyOverview: ActivityOverviewResult = {
  summary: {
    dateUtc: '2026-07-16',
    totalDurationSeconds: 0,
    activeDurationSeconds: 0,
    idleDurationSeconds: 0,
    unknownDurationSeconds: 0,
    sessionCount: 0,
  },
  topApps: [],
  recentSessions: [],
};

describe('dashboardModel', () => {
  it('只有摘要与列表都为空时进入 Empty', () => {
    expect(isOverviewEmpty(emptyOverview)).toBe(true);
    expect(isOverviewEmpty({
      ...emptyOverview,
      topApps: [{
        displayName: 'Visual Studio Code',
        totalDurationSeconds: 60,
        activeDurationSeconds: 60,
        idleDurationSeconds: 0,
        unknownDurationSeconds: 0,
        sessionCount: 1,
      }],
    })).toBe(false);
  });

  it('页面隐藏时将轮询间隔从十五秒降低为六十秒', () => {
    expect(getOverviewRefreshInterval(true)).toBe(overviewVisibleRefreshInterval);
    expect(getOverviewRefreshInterval(false)).toBe(overviewHiddenRefreshInterval);
    expect(overviewHiddenRefreshInterval).toBeGreaterThan(overviewVisibleRefreshInterval);
  });
});
