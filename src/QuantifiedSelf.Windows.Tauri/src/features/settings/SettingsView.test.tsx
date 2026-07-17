import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { SettingsSnapshot } from '../../bridge/contracts';
import { createSettingsDraft, updateSettingsDraft } from './settingsModel';
import { SettingsLoadError, SettingsLoading, SettingsView } from './SettingsView';

const settings: SettingsSnapshot = {
  appSettings: {
    theme: 'dark',
    refreshIntervalSeconds: 1234,
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

const baseline = createSettingsDraft(settings);

function renderReady(overrides: Partial<Parameters<typeof SettingsView>[0]> = {}) {
  const props: Parameters<typeof SettingsView>[0] = {
    draft: updateSettingsDraft(baseline, 'appSettings.theme', 'light'),
    baseline,
    dirty: true,
    saveState: 'ready',
    fieldErrors: {},
    onChange: vi.fn(),
    onSave: vi.fn(),
    onRetrySave: vi.fn(),
    onRestoreDefaults: vi.fn(),
    onDiscard: vi.fn(),
    locale: 'de-DE',
    ...overrides,
  };
  const view = render(<SettingsView {...props} />);
  return { props, ...view };
}

describe('SettingsView', () => {
  it('Loading 和读取 Error 使用可读状态并支持重试', () => {
    const retry = vi.fn();
    const { unmount } = render(<SettingsLoading />);
    expect(screen.getByRole('status')).toHaveAttribute('aria-busy', 'true');
    unmount();

    render(<SettingsLoadError message="无法连接本地服务，请稍后重试。" retrying={false} onRetry={retry} />);
    expect(screen.getByRole('alert')).toHaveTextContent('无法连接本地服务');
    fireEvent.click(screen.getByRole('button', { name: '重试' }));
    expect(retry).toHaveBeenCalledOnce();
  });

  it('Ready 使用分组原生表单、显式标签和 locale-aware 已保存值', () => {
    const { props } = renderReady();

    expect(screen.getByRole('heading', { name: '外观与刷新' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Agent 采集参数' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '记录与隐私' })).toBeInTheDocument();
    expect(screen.getByLabelText('界面刷新间隔')).toHaveAttribute('inputmode', 'numeric');
    expect(screen.getByText(/当前已保存：1\.234 秒/)).toBeInTheDocument();
    expect(screen.getByText('有未保存修改')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('主题'), { target: { value: 'high_contrast' } });
    expect(props.onChange).toHaveBeenCalledWith('appSettings.theme', 'high_contrast');
    fireEvent.click(screen.getByRole('button', { name: '保存设置' }));
    expect(props.onSave).toHaveBeenCalledOnce();
  });

  it('字段错误与控件关联，并通过 alert 提供安全保存错误', () => {
    renderReady({
      saveState: 'error',
      errorMessage: '部分设置值无效。',
      fieldErrors: { 'agentOptions.retentionDays': '请检查此设置值。' },
    });

    const input = screen.getByLabelText('数据保留时间');
    const error = screen.getByText('请检查此设置值。');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input.getAttribute('aria-describedby')).toContain(error.id);
    expect(screen.getByRole('alert')).toHaveTextContent('部分设置值无效');
  });

  it('Saving 禁用字段和操作，防止重复提交并公告状态', () => {
    const save = vi.fn();
    renderReady({ saveState: 'saving', onSave: save });

    expect(screen.getByRole('group', { name: '可编辑设置' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '正在保存…' })).toBeDisabled();
    const form = screen.getByRole('button', { name: '正在保存…' }).closest('form');
    expect(form).not.toBeNull();
    if (form) fireEvent.submit(form);
    expect(save).toHaveBeenCalledOnce();
    expect(screen.getByText('正在保存设置，请稍候。')).toBeInTheDocument();
  });

  it('Success、恢复默认值、放弃修改和重试均使用原生按钮', () => {
    const restore = vi.fn();
    const discard = vi.fn();
    const retry = vi.fn();
    const { rerender } = renderReady({
      saveState: 'success',
      onRestoreDefaults: restore,
      onDiscard: discard,
    });
    expect(screen.getByText('设置已保存，并已重新读取本地结果。')).toHaveAttribute('role', 'status');
    fireEvent.click(screen.getByRole('button', { name: '恢复默认值' }));
    fireEvent.click(screen.getByRole('button', { name: '放弃修改' }));
    expect(restore).toHaveBeenCalledOnce();
    expect(discard).toHaveBeenCalledOnce();

    rerender(<SettingsView draft={baseline} baseline={baseline} dirty saveState="error" fieldErrors={{}} errorMessage="保存失败" onChange={vi.fn()} onSave={vi.fn()} onRetrySave={retry} onRestoreDefaults={vi.fn()} onDiscard={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: '重试保存' }));
    expect(retry).toHaveBeenCalledOnce();
  });
});
