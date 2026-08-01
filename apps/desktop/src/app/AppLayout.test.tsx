import { act, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { vi } from 'vitest';
import AppLayout from './AppLayout';
import type { AgentStatusDto, Int64String } from '../types/wuji-core';

/** Int64String 夹具断言（R07 品牌类型）。 */
const i64 = (text: string): Int64String => text as Int64String;

const invoke = vi.fn<(command: string, args?: unknown) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: unknown): Promise<unknown> => invoke(command, args),
}));

function statusFixture(
  captureState: AgentStatusDto['captureState'],
  processState: AgentStatusDto['processState'] = 'running',
): AgentStatusDto {
  return {
    agentVersion: '0.1.0',
    protocolVersion: 1,
    schemaVersion: 1,
    processState,
    captureState,
    writerState: 'healthy',
    runtimeId: '01J0000000000000000000000X',
    heartbeatAtUtcMs: null,
    lastObservationAtUtcMs: null,
    lastWriteAtUtcMs: null,
    captureQueueDepth: 0,
    writerQueueDepth: 0,
    droppedCaptureCount: i64('0'),
    droppedWriterCount: i64('0'),
    safeErrorCode: null,
  };
}

function renderLayout() {
  return render(
    <MemoryRouter initialEntries={['/']}>
      <AppLayout />
    </MemoryRouter>,
  );
}

describe('AppLayout 顶栏', () => {
  beforeEach(() => {
    invoke.mockReset();
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_get_status') {
        return Promise.resolve(statusFixture('stopped', 'stopped'));
      }
      if (command === 'auto_start_status') {
        return Promise.resolve({ state: 'idle', error: null });
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
  });

  it('启动编排进行中显示“正在开始记录…”瞬态', async () => {
    // Agent 尚未可达（status 为 stopped/stopped），但 Host 侧 auto_start_status
    // 提供 starting 瞬态：只有收到 Agent 确认后顶栏才显示记录中。
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_get_status') {
        return Promise.resolve(statusFixture('stopped', 'stopped'));
      }
      if (command === 'auto_start_status') {
        return Promise.resolve({ state: 'starting', error: null });
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    renderLayout();
    await waitFor(() => {
      expect(screen.getByTestId('capture-state-badge')).toHaveTextContent('正在开始记录…');
    });
  });

  it('自动开始记录失败显示可见提示（不只在 stderr）', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_get_status') {
        return Promise.resolve(statusFixture('stopped', 'stopped'));
      }
      if (command === 'auto_start_status') {
        return Promise.resolve({
          state: 'failed',
          error: { code: 'AGENT_WRITER_FAULTED', message: '写入器故障且无法恢复，采集保持停止' },
        });
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    renderLayout();
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('自动开始记录失败：写入器故障且无法恢复');
    });
    // 失败不伪装成功：启动按钮仍然可用，用户可手动重试。
    expect(screen.getByRole('button', { name: '启动并记录' })).toBeInTheDocument();
  });

  it('自动启动失败后手动重试成功，红色提示消失', async () => {
    let retried = false;
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_get_status') {
        return Promise.resolve(
          retried ? statusFixture('running') : statusFixture('stopped', 'stopped'),
        );
      }
      if (command === 'auto_start_status') {
        // Host 侧：手动 capture_start 成功后 AutoStartOutcome 被清除（mark_idle）。
        return Promise.resolve(
          retried ? { state: 'idle', error: null } : {
            state: 'failed',
            error: { code: 'AGENT_WRITER_FAULTED', message: '写入器故障且无法恢复，采集保持停止' },
          },
        );
      }
      if (command === 'capture_start') {
        retried = true;
        return Promise.resolve(statusFixture('running'));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    renderLayout();
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('自动开始记录失败');
    });
    screen.getByRole('button', { name: '启动并记录' }).click();
    // 重试成功后：错误提示消失，状态如实显示“正在记录”。
    await waitFor(() => {
      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(screen.getByTestId('capture-state-badge')).toHaveTextContent('正在记录');
    });
  });

  it('手动重试后的新状态不被更早发出的迟到轮询覆盖', async () => {
    let resolveStale: ((value: unknown) => void) | null = null;
    const stale = new Promise<unknown>((resolve) => {
      resolveStale = resolve;
    });
    let autoStatusCalls = 0;
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_get_status') {
        return Promise.resolve(statusFixture('stopped', 'stopped'));
      }
      if (command === 'auto_start_status') {
        autoStatusCalls += 1;
        return autoStatusCalls === 1
          ? stale
          : Promise.resolve({ state: 'idle', error: null });
      }
      if (command === 'capture_start') {
        return Promise.resolve(statusFixture('running'));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });

    renderLayout();
    const start = await screen.findByRole('button', { name: '启动并记录' });
    start.click();
    await waitFor(() => {
      expect(autoStatusCalls).toBeGreaterThanOrEqual(2);
      expect(screen.getByTestId('capture-state-badge')).toHaveTextContent('正在记录');
    });

    await act(async () => {
      resolveStale?.({
        state: 'failed',
        error: { code: 'AGENT_WRITER_FAULTED', message: '迟到的旧失败' },
      });
      await stale;
    });
    expect(screen.queryByText(/迟到的旧失败/)).not.toBeInTheDocument();
  });

  it('Agent 未运行时显示启动并记录按钮、主题切换', async () => {
    renderLayout();
    await waitFor(() => {
      expect(screen.getByTestId('capture-state-badge')).toHaveTextContent('Agent 未运行');
    });
    expect(screen.getByRole('button', { name: '启动并记录' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '切换主题' })).toBeInTheDocument();
  });

  it('运行中但未记录时区分进程与采集状态', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_get_status') {
        return Promise.resolve(statusFixture('stopped'));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    renderLayout();
    await waitFor(() => {
      expect(screen.getByTestId('capture-state-badge')).toHaveTextContent('未记录');
    });
    expect(screen.getByRole('button', { name: '开始记录' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '停止 Agent' })).toBeInTheDocument();
  });

  it('running 状态显示暂停与停止 Agent', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_get_status') {
        return Promise.resolve(statusFixture('running'));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    renderLayout();
    await waitFor(() => {
      expect(screen.getByTestId('capture-state-badge')).toHaveTextContent('正在记录');
    });
    expect(screen.getByRole('button', { name: '暂停' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '停止 Agent' })).toBeInTheDocument();
  });

  it('停止 Agent 后切换到未运行状态', async () => {
    let stopped = false;
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_get_status') {
        return Promise.resolve(
          stopped ? statusFixture('stopped', 'stopped') : statusFixture('running'),
        );
      }
      if (command === 'agent_process_stop') {
        stopped = true;
        return Promise.resolve(statusFixture('stopped', 'stopped'));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    renderLayout();
    const stop = await screen.findByRole('button', { name: '停止 Agent' });
    stop.click();
    await waitFor(() => {
      expect(screen.getByTestId('capture-state-badge')).toHaveTextContent('Agent 未运行');
    });
    expect(invoke).toHaveBeenCalledWith('agent_process_stop', undefined);
  });

  it('Agent 未运行时启动并记录', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_get_status') {
        return Promise.resolve(statusFixture('stopped', 'stopped'));
      }
      if (command === 'capture_start') {
        return Promise.resolve(statusFixture('running'));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    renderLayout();
    const start = await screen.findByRole('button', { name: '启动并记录' });
    start.click();
    await waitFor(() => {
      expect(screen.getByTestId('capture-state-badge')).toHaveTextContent('正在记录');
    });
    expect(invoke).toHaveBeenCalledWith('capture_start', undefined);
  });

  it('导航包含五个页面入口', async () => {
    renderLayout();
    await waitFor(() => {
      expect(screen.getByRole('link', { name: '今日' })).toBeInTheDocument();
    });
    expect(screen.getByRole('link', { name: '时间线' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '热力图' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '设置' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '诊断' })).toBeInTheDocument();
  });
});
