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

function prefsFixture(overrides: Partial<{ autoStartRecordingWhenAppStarts: boolean }> = {}) {
  return { autoStartRecordingWhenAppStarts: true, ...overrides };
}

describe('Settings 页面', () => {
  beforeEach(() => {
    invoke.mockReset();
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'desktop_prefs_get') {
        return Promise.resolve(prefsFixture());
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
  });

  it('展示 Settings 字段与 Desktop 本地偏好、saved/applied revision', async () => {
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('采样间隔')).toHaveValue('3');
    });
    expect(screen.getByLabelText('空闲阈值（秒）')).toHaveValue('60');
    expect(screen.getByLabelText('工作块打断阈值（秒）')).toHaveValue('300');
    expect(screen.getByLabelText('排除的应用（每行一个进程名）')).toHaveValue('keepass.exe');
    expect(screen.getByLabelText('登录 Windows 后开始记录')).not.toBeChecked();
    expect(screen.getByLabelText('启动吾迹时自动开始记录')).toBeChecked();
    expect(screen.getAllByText('1', { exact: false }).length).toBeGreaterThan(0);
  });

  it('只改 Settings 时按 expectedRevision 提交，且不得调用 desktop_prefs_update', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'desktop_prefs_get') {
        return Promise.resolve(prefsFixture());
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
    fireEvent.change(screen.getByLabelText('采样间隔'), { target: { value: '5' } });
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('已保存并应用（revision 2）');
    });
    expect(invoke).toHaveBeenCalledWith('settings_update', {
      patch: {
        samplingIntervalSeconds: 5,
        idleThresholdSeconds: 60,
        workBreakIdleSeconds: 300,
        excludedProcessNames: ['keepass.exe'],
        startCaptureOnLogin: false,
        expectedRevision: '1',
      },
    });
    // 只改 Settings 不得调用 desktop_prefs_update（不触碰 Desktop 偏好）。
    expect(invoke).not.toHaveBeenCalledWith('desktop_prefs_update', expect.anything());
  });

  it('只改“启动吾迹时自动开始记录”保存走 desktop_prefs_update，且不得调用 settings_update', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'desktop_prefs_get') {
        return Promise.resolve(prefsFixture());
      }
      if (command === 'desktop_prefs_update') {
        return Promise.resolve(prefsFixture({ autoStartRecordingWhenAppStarts: false }));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('启动吾迹时自动开始记录')).toBeChecked();
    });
    fireEvent.click(screen.getByLabelText('启动吾迹时自动开始记录'));
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('已保存本地偏好');
    });
    expect(invoke).toHaveBeenCalledWith('desktop_prefs_update', {
      patch: { autoStartRecordingWhenAppStarts: false },
    });
    // 只改偏好不得推进 Settings revision、不得触发 Barrier/effectivity。
    expect(invoke).not.toHaveBeenCalledWith('settings_update', expect.anything());
  });

  it('无变更时保存不调用任何 update', async () => {
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('采样间隔')).toHaveValue('3');
    });
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('没有需要保存的变更');
    });
    expect(invoke).not.toHaveBeenCalledWith('settings_update', expect.anything());
    expect(invoke).not.toHaveBeenCalledWith('desktop_prefs_update', expect.anything());
  });

  it('Settings 保存失败时偏好仍保存（部分失败矩阵）', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'desktop_prefs_get') {
        return Promise.resolve(prefsFixture());
      }
      if (command === 'settings_update') {
        return Promise.reject(
          Object.assign(new Error('Agent 离线无法应用设置'), { code: 'AGENT_WRITER_FAULTED' }),
        );
      }
      if (command === 'desktop_prefs_update') {
        return Promise.resolve(prefsFixture({ autoStartRecordingWhenAppStarts: false }));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('采样间隔')).toHaveValue('3');
    });
    fireEvent.change(screen.getByLabelText('采样间隔'), { target: { value: '5' } });
    fireEvent.click(screen.getByLabelText('启动吾迹时自动开始记录'));
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Agent 离线无法应用设置');
    });
    // Settings 失败不阻断偏好：desktop_prefs_update 必须已提交。
    expect(invoke).toHaveBeenCalledWith('desktop_prefs_update', {
      patch: { autoStartRecordingWhenAppStarts: false },
    });
    expect(screen.getByRole('alert')).toHaveTextContent('本地偏好已保存');
  });

  it('Desktop 偏好损坏时页面仍可用，合法保存自愈后警告消失', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'desktop_prefs_get') {
        return Promise.reject(
          Object.assign(new Error('Desktop 偏好文件损坏，无法读取；将使用默认值并在下次保存时修复'), {
            code: 'SETTINGS_INVALID',
          }),
        );
      }
      if (command === 'desktop_prefs_update') {
        return Promise.resolve(prefsFixture({ autoStartRecordingWhenAppStarts: false }));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('采样间隔')).toHaveValue('3');
    });
    // 损坏被显式上报，不伪装成默认值；表单按默认 true 继续可用。
    expect(screen.getByRole('note')).toHaveTextContent('偏好文件损坏');
    expect(screen.getByLabelText('启动吾迹时自动开始记录')).toBeChecked();
    fireEvent.click(screen.getByLabelText('启动吾迹时自动开始记录'));
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('已保存本地偏好');
      expect(screen.queryByRole('note')).not.toBeInTheDocument();
    });
  });

  it('两者都改且偏好保存失败时，Settings 成功仍被确认', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'desktop_prefs_get') {
        return Promise.resolve(prefsFixture());
      }
      if (command === 'settings_update') {
        return Promise.resolve(dtoFixture({ revision: '2', appliedRevision: '2' }));
      }
      if (command === 'desktop_prefs_update') {
        return Promise.reject(Object.assign(new Error('写盘失败'), { code: 'DB_UNAVAILABLE' }));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('采样间隔')).toHaveValue('3');
    });
    fireEvent.change(screen.getByLabelText('采样间隔'), { target: { value: '5' } });
    fireEvent.click(screen.getByLabelText('启动吾迹时自动开始记录'));
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('设置已保存并应用（revision 2），但本地偏好保存失败');
    });
    expect(invoke).toHaveBeenCalledWith('settings_update', expect.anything());
    expect(invoke).toHaveBeenCalledWith('desktop_prefs_update', expect.anything());
  });

  it('只改偏好且偏好保存失败 → 错误提示，不调用 settings_update', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'desktop_prefs_get') {
        return Promise.resolve(prefsFixture());
      }
      if (command === 'desktop_prefs_update') {
        return Promise.reject(Object.assign(new Error('写盘失败'), { code: 'DB_UNAVAILABLE' }));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    render(<SettingsPage />);
    await waitFor(() => {
      expect(screen.getByLabelText('启动吾迹时自动开始记录')).toBeChecked();
    });
    fireEvent.click(screen.getByLabelText('启动吾迹时自动开始记录'));
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('写盘失败');
    });
    expect(invoke).toHaveBeenCalledWith('desktop_prefs_update', expect.anything());
    expect(invoke).not.toHaveBeenCalledWith('settings_update', expect.anything());
  });

  it('SETTINGS_CONFLICT 刷新并提示', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'settings_get') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'desktop_prefs_get') {
        return Promise.resolve(prefsFixture());
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
    fireEvent.change(screen.getByLabelText('采样间隔'), { target: { value: '5' } });
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('已为你刷新最新值');
    });
    // 冲突时不得触碰偏好（偏好未改）。
    expect(invoke).not.toHaveBeenCalledWith('desktop_prefs_update', expect.anything());
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
      if (command === 'desktop_prefs_get') {
        return Promise.resolve(prefsFixture());
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
    fireEvent.change(screen.getByLabelText('采样间隔'), { target: { value: '5' } });
    screen.getByRole('button', { name: '保存' }).click();
    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('Agent 将在下次连接时应用');
    });
  });
});
