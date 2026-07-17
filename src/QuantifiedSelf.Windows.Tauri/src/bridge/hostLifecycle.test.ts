import { beforeEach, describe, expect, it, vi } from 'vitest';
import { listen } from '@tauri-apps/api/event';
import {
  hostCloseRequestedEvent,
  subscribeHostCloseRequested,
} from './hostLifecycle';

vi.mock('@tauri-apps/api/event', () => ({
  listen: vi.fn(),
}));

describe('host lifecycle events', () => {
  beforeEach(() => vi.clearAllMocks());

  it('只订阅固定关闭事件并转发安全 intent', async () => {
    const unlisten = vi.fn<() => void>();
    let listener: ((event: { payload: { intent: unknown } }) => void) | undefined;
    vi.mocked(listen).mockImplementation(async (_event, handler) => {
      await Promise.resolve();
      listener = handler as typeof listener;
      return unlisten;
    });
    const handler = vi.fn();

    const remove = await subscribeHostCloseRequested(handler);
    listener?.({ payload: { intent: 'hide' } });
    listener?.({ payload: { intent: 'exit' } });

    expect(listen).toHaveBeenCalledWith(hostCloseRequestedEvent, expect.any(Function));
    expect(handler).toHaveBeenNthCalledWith(1, 'hide');
    expect(handler).toHaveBeenNthCalledWith(2, 'exit');
    remove();
    expect(unlisten).toHaveBeenCalledOnce();
  });

  it('忽略未知、路径或进程形态的事件负载', async () => {
    let listener: ((event: { payload: { intent: unknown } }) => void) | undefined;
    const unlisten = vi.fn<() => void>();
    vi.mocked(listen).mockImplementation(async (_event, handler) => {
      await Promise.resolve();
      listener = handler as typeof listener;
      return unlisten;
    });
    const handler = vi.fn();
    await subscribeHostCloseRequested(handler);

    listener?.({ payload: { intent: 'kill-process' } });
    listener?.({ payload: { intent: { path: 'private', processId: 42 } } });
    listener?.({ payload: { intent: null } });

    expect(handler).not.toHaveBeenCalled();
  });
});
