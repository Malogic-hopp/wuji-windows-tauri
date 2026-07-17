import { describe, expect, it } from 'vitest';
import type { SettingsSnapshot } from '../../bridge/contracts';
import {
  createSettingsDraft,
  formatSettingsInteger,
  isSettingsDirty,
  mapBridgeFieldErrors,
  parseSettingsDraft,
  updateSettingsDraft,
} from './settingsModel';

const settings: SettingsSnapshot = {
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
};

describe('settingsModel', () => {
  it('在 SettingsSnapshot 和可编辑草稿之间无损转换', () => {
    const draft = createSettingsDraft(settings);
    const parsed = parseSettingsDraft(draft);

    expect(parsed.fieldErrors).toEqual({});
    expect(parsed.settings).toEqual(settings);
    expect(isSettingsDirty(draft, createSettingsDraft(settings))).toBe(false);
  });

  it('只做整数格式校验，不复制 Application 范围规则', () => {
    const baseline = createSettingsDraft(settings);
    const empty = updateSettingsDraft(baseline, 'agentOptions.samplingIntervalSeconds', '');
    const decimal = updateSettingsDraft(baseline, 'agentOptions.retentionDays', '1.5');
    const serverOwnedRange = updateSettingsDraft(baseline, 'agentOptions.samplingIntervalSeconds', '-1');

    expect(parseSettingsDraft(empty).fieldErrors['agentOptions.samplingIntervalSeconds']).toBe('请输入整数。');
    expect(parseSettingsDraft(decimal).fieldErrors['agentOptions.retentionDays']).toBe('请输入不带小数的整数。');
    expect(parseSettingsDraft(serverOwnedRange).settings?.agentOptions.samplingIntervalSeconds).toBe(-1);
  });

  it('只接受 allowlist 字段错误并使用固定安全文案', () => {
    const errors = mapBridgeFieldErrors([
      { field: 'agentOptions.retentionDays', message: 'private database path' },
      { field: 'settings.privatePath', message: 'C:\\Users\\private' },
    ]);

    expect(errors).toEqual({ 'agentOptions.retentionDays': '请检查此设置值。' });
    expect(JSON.stringify(errors)).not.toContain('private');
  });

  it('使用指定 locale 展示整数', () => {
    expect(formatSettingsInteger(1234, 'de-DE')).toBe('1.234');
    expect(formatSettingsInteger(1234, 'en-US')).toBe('1,234');
  });
});
