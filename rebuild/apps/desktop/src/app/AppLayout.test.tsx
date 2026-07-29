import { render, screen, waitFor } from '@testing-library/react';
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

function statusFixture(captureState: AgentStatusDto['captureState']): AgentStatusDto {
  return {
    agentVersion: '0.1.0',
    protocolVersion: 1,
    schemaVersion: 1,
    processState: 'running',
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
      if (command === 'agent_process_ensure_running' || command === 'agent_get_status') {
        return Promise.resolve(statusFixture('stopped'));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
  });

  it('stopped 状态显示"未记录"与开始记录按钮、主题切换', async () => {
    renderLayout();
    await waitFor(() => {
      expect(screen.getByTestId('capture-state-badge')).toHaveTextContent('未记录');
    });
    expect(screen.getByRole('button', { name: '开始记录' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '切换主题' })).toBeInTheDocument();
  });

  it('running 状态显示暂停与停止', async () => {
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_process_ensure_running' || command === 'agent_get_status') {
        return Promise.resolve(statusFixture('running'));
      }
      return Promise.reject(new Error(`unexpected: ${command}`));
    });
    renderLayout();
    await waitFor(() => {
      expect(screen.getByTestId('capture-state-badge')).toHaveTextContent('正在记录');
    });
    expect(screen.getByRole('button', { name: '暂停' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '停止' })).toBeInTheDocument();
  });

  it('导航包含四个页面入口', async () => {
    renderLayout();
    await waitFor(() => {
      expect(screen.getByRole('link', { name: '今日' })).toBeInTheDocument();
    });
    expect(screen.getByRole('link', { name: '时间线' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '设置' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: '诊断' })).toBeInTheDocument();
  });
});
