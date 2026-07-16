import { describe, expect, it } from 'vitest';
import {
  formatCount,
  formatDuration,
  formatLastUpdated,
  formatSessionRange,
} from './dashboardFormatting';

describe('dashboardFormatting', () => {
  it('使用 locale-aware 单位格式化 duration 和计数', () => {
    expect(formatDuration(0, 'zh-CN')).toBe('0秒');
    expect(formatDuration(3_660, 'zh-CN')).toBe('1小时1分钟');
    expect(formatCount(12_345, 'zh-CN')).toBe('12,345');
  });

  it('使用本地时区格式化会话区间和最后更新时间', () => {
    const start = new Date(2026, 6, 16, 9, 15, 0);
    const end = new Date(2026, 6, 16, 10, 30, 0);

    expect(formatSessionRange(start.toISOString(), end.toISOString(), 'zh-CN'))
      .toContain('7月16日 09:15');
    expect(formatSessionRange(start.toISOString(), undefined, 'zh-CN')).toContain('起');
    expect(formatLastUpdated(end.getTime(), 'zh-CN')).toContain('10:30');
  });

  it('对无效时间返回安全占位文本', () => {
    expect(formatSessionRange('private database error', undefined, 'zh-CN')).toBe('时间未知');
    expect(formatLastUpdated(0, 'zh-CN')).toBe('尚未更新');
  });
});
