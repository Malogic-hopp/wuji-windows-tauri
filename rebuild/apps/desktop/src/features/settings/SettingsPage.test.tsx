import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import SettingsPage from './SettingsPage';
import type { SettingsDto } from '../../types/wuji-core';

const invoke = vi.fn<(command: string, args?: unknown) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: unknown): Promise<unknown> => invoke(command, args),
}));

function dtoFixture(overrides: Partial<SettingsDto> = {}): SettingsDto {
  return {
    schemaVersion: 1,
    revision: '1',
    persisted: true,
    appliedRevision: '1',
    samplingIntervalSeconds: 3,
    idleThresholdSeconds: 60,
    workBreakIdleSeconds: 300,
    excludedProcessNames: ['keepass.exe'],
    startCaptureOnLogin: false,
    ...overrides,
  };
}

describe('Settings 页面', () => {
  beforeEach(() => {
    invoke.mockReset();
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
  });

  it('展示六个字段与 saved/applied revision', async () => {
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('采样间隔')).toHaveValue('3');
    });
    expect(screen.getByLabelText('空闲阈值（秒）')).toHaveValue('60');
    expect(screen.getByLabelText('工作块打断阈值（秒）')).toHaveValue('300');
    expect(screen.getByLabelText('排除的应用（每行一个进程名）')).toHaveValue('keepass.exe');
    expect(screen.getByLabelText('登录 Windows 后开始记录')).not.toBeChecked();
    expect(screen.getAllByText('1', { exact: false }).length).toBeGreaterThan(0);
  });

  it('保存时按 expectedRevision 提交并显示成功', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'settings_update') {
        return Promise.resolve(dtoFixture({ revision: '2', appliedRevision: '2' }));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('采样间隔')).toHaveValue('3');
    });
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('已保存并应用（revision 2）');
    });
    expect(invoke).toHaveBeenCalledWith('settings_update', {
      patch: {
        samplingIntervalSeconds: 3,
        idleThresholdSeconds: 60,
        workBreakIdleSeconds: 300,
        excludedProcessNames: ['keepass.exe'],
        startCaptureOnLogin: false,
        expectedRevision: '1',
      },
    });
  });

  it('SETTINGS_CONFLICT 刷新并提示', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'settings_update') {
        return Promise.reject(Object.assign(new Error('冲突'), { code: 'SETTINGS_CONFLICT' }));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('采样间隔')).toHaveValue('3');
    });
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('已为你刷新最新值');
    });
  });

  it('字段校验：工作块打断阈值必须大于空闲阈值', async () => {
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('采样间隔')).toHaveValue('3');
    });
    fireEvent.change(screen.getByLabelText('空闲阈值（秒）'), {
      target: { value: '300' },
    });
    fireEvent.change(screen.getByLabelText('工作块打断阈值（秒）'), {
      target: { value: '300' },
    });
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('工作块打断阈值必须大于空闲阈值');
    });
  });

  it('saved-not-applied 显示警告', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'settings_update') {
        return Promise.reject(Object.assign(new Error('已保存'), { code: 'SETTINGS_SAVED_NOT_APPLIED' }));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('采样间隔')).toHaveValue('3');
    });
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('Agent 将在下次连接时应用');
    });
  });
});
