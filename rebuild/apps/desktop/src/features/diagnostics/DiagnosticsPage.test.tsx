import { render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import DiagnosticsPage from './DiagnosticsPage';
import type { DiagnosticsDto } from '../../bridge/client';

const invoke = vi.fn<(command: string, args?: unknown) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: unknown): Promise<unknown> => invoke(command, args),
}));

function dtoFixture(overrides: Partial<DiagnosticsDto> = {}): DiagnosticsDto {
  return {
    status: {
      agentVersion: '0.1.0',
      protocolVersion: 1,
      schemaVersion: 1,
      processState: 'running',
      captureState: 'running',
      writerState: 'healthy',
      runtimeId: '01J0000000000000000000000X',
      heartbeatAtUtcMs: String(Date.now() - 2000),
      lastObservationAtUtcMs: String(Date.now() - 3000),
      lastWriteAtUtcMs: String(Date.now() - 3000),
      captureQueueDepth: 0,
      writerQueueDepth: 0,
      droppedCaptureCount: '0',
      droppedWriterCount: '0',
      safeErrorCode: null,
    },
    databaseReachable: true,
    settingsPersisted: true,
    appliedRevision: '3',
    reportingTimeZoneId: 'Asia/Shanghai',
    dataRootMasked: '%LOCALAPPDATA%\\WUJI-Rebuild-V01\\dev',
    agentExeMasked: '%LOCALAPPDATA%\\Agent\\wuji-rebuild-agent-v01.exe',
    ...overrides,
  };
}

describe('Diagnostics 页面', () => {
  beforeEach(() => {
    invoke.mockReset();
  });

  it('展示运行健康、最后活动与修复操作', async () => {
    invoke.mockResolvedValue(dtoFixture());
    render(<DiagnosticsPage />);
    await waitFor(() => {
      expect(screen.getByText('已连接')).toBeInTheDocument();
    });
    expect(screen.getByText('正在记录')).toBeInTheDocument();
    expect(screen.getByText('可读')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '按当前设置重新同步登录启动' })).toBeInTheDocument();
  });

  it('resync 调用命令并显示结果', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'diagnostics_get_summary') {
        return Promise.resolve(dtoFixture());
      }
      if (command === 'settings_resync_login_startup') {
        return Promise.resolve({});
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    render(<DiagnosticsPage />);
    await waitFor(() => {
      expect(screen.getByRole('button', { name: '按当前设置重新同步登录启动' })).toBeInTheDocument();
    });
    screen.getByRole('button', { name: '按当前设置重新同步登录启动' }).click();
    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('已按当前设置重新同步登录启动');
    });
  });

  it('高级信息默认折叠且路径脱敏', async () => {
    invoke.mockResolvedValue(dtoFixture());
    render(<DiagnosticsPage />);
    await waitFor(() => {
      expect(screen.getByText('高级信息（默认折叠，路径已脱敏）')).toBeInTheDocument();
    });
    expect(screen.getByText(/%LOCALAPPDATA%\\WUJI-Rebuild-V01\\dev/)).toBeInTheDocument();
    expect(screen.queryByText(/C:\\Users/)).not.toBeInTheDocument();
  });

  it('Agent 离线且数据库不可读时显示 Error', async () => {
    invoke.mockResolvedValue(dtoFixture({ status: null, databaseReachable: false }));
    render(<DiagnosticsPage />);
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('无法连接 Agent，数据库也不可读');
    });
  });
});
