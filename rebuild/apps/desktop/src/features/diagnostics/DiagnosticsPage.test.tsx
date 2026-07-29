import { render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import DiagnosticsPage from './DiagnosticsPage';
import type { DiagnosticsDto } from '../../bridge/client';
import type { AgentStatusDto, Int64String } from '../../types/wuji-core';

/** Int64String 夹具断言（R07 品牌类型）。 */
const i64 = (text: string): Int64String => text as Int64String;

const invoke = vi.fn<(command: string, args?: unknown) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: unknown): Promise<unknown> => invoke(command, args),
}));

function statusFixture(overrides: Partial<AgentStatusDto> = {}): AgentStatusDto {
  return {
    agentVersion: '0.1.0',
    protocolVersion: 1,
    schemaVersion: 1,
    processState: 'running',
    captureState: 'running',
    writerState: 'healthy',
    runtimeId: '01J0000000000000000000000X',
    heartbeatAtUtcMs: i64(String(Date.now() - 2000)),
    lastObservationAtUtcMs: i64(String(Date.now() - 3000)),
    lastWriteAtUtcMs: i64(String(Date.now() - 3000)),
    captureQueueDepth: 0,
    writerQueueDepth: 0,
    droppedCaptureCount: i64('0'),
    droppedWriterCount: i64('0'),
    safeErrorCode: null,
    ...overrides,
  };
}

function dtoFixture(overrides: Partial<DiagnosticsDto> = {}): DiagnosticsDto {
  return {
    status: statusFixture(),
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

  it('非零队列深度如实显示（R09）', async () => {
    invoke.mockResolvedValue(
      dtoFixture({
        status: statusFixture({ captureQueueDepth: 7, writerQueueDepth: 3 }),
      }),
    );
    render(<DiagnosticsPage />);
    await waitFor(() => {
      expect(screen.getByText('采集 7 · 写入 3')).toBeInTheDocument();
    });
  });

  it('相对年龄随轮询更新（R09：时间基准不冻结在首次渲染）', async () => {
    const fixed = Date.now() - 2000;
    invoke.mockImplementation((command: string) =>
      command === 'diagnostics_get_summary'
        ? Promise.resolve(
            dtoFixture({
              status: statusFixture({ heartbeatAtUtcMs: i64(String(fixed)) }),
            }),
          )
        : Promise.reject(new Error(`unexpected: ${command}`)),
    );
    render(<DiagnosticsPage />);
    await waitFor(() => {
      expect(screen.getAllByText(/秒前/).length).toBeGreaterThan(0);
    });
    const firstAges = screen.getAllByText(/秒前/).map((node) => node.textContent);
    // 下一轮 2 秒轮询后，年龄文本必须变化（now 基准随 dto 一起更新）。
    await waitFor(
      () => {
        const current = screen.getAllByText(/秒前/).map((node) => node.textContent);
        expect(current.some((text, index) => text !== firstAges[index])).toBe(true);
      },
      { timeout: 6000 },
    );
  });
});
