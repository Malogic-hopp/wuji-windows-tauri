import { render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import App from './App';

const invoke = vi.fn<(command: string, args?: unknown) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: unknown): Promise<unknown> => invoke(command, args),
}));

describe('V01-6 占位壳', () => {
  beforeEach(() => {
    invoke.mockReset();
    invoke.mockImplementation((command: string) => {
      if (command === 'agent_get_status') {
        return Promise.resolve({
          agentVersion: '0.1.0',
          protocolVersion: 1,
          schemaVersion: 1,
          processState: 'running',
          captureState: 'running',
          writerState: 'healthy',
          runtimeId: '01J0000000000000000000000X',
          heartbeatAtUtcMs: '1784300000000',
          lastObservationAtUtcMs: null,
          lastWriteAtUtcMs: null,
          captureQueueDepth: 0,
          writerQueueDepth: 0,
          droppedCaptureCount: '0',
          droppedWriterCount: '0',
          safeErrorCode: null,
        });
      }
      if (command === 'activity_get_today') {
        return Promise.resolve({
          localDate: '2026-07-19',
          reportingTimeZoneId: 'Asia/Shanghai',
          activeDurationMs: '3600000',
          currentApp: null,
          lastApp: null,
          longestWorkBlockActiveMs: '1800000',
          workBlockCount: '2',
          rawAppSwitchCount: '3',
          topApps: [],
          quality: { isComplete: true, gapCount: '0', droppedCount: '0' },
        });
      }
      return Promise.reject(new Error(`unexpected command: ${command}`));
    });
  });

  it('展示 Agent 状态与今日读数', async () => {
    render(<App />);
    await waitFor(() => {
      expect(screen.getByTestId('capture-state')).toHaveTextContent('正在记录');
    });
    await waitFor(() => {
      expect(screen.getByText(/活跃时长/).textContent).toContain('3600000');
    });
    expect(invoke).toHaveBeenCalledWith('agent_get_status', undefined);
    expect(invoke).toHaveBeenCalledWith('activity_get_today', undefined);
  });

  it('命令失败时展示安全错误', async () => {
    invoke.mockImplementation(() => Promise.reject(new Error('DB_UNAVAILABLE')));
    render(<App />);
    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('DB_UNAVAILABLE');
    });
  });
});
