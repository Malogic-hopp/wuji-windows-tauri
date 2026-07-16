import { beforeEach, describe, expect, it, vi } from 'vitest';
import { listen } from '@tauri-apps/api/event';
import {
  bridgeAvailabilityEventName,
  listenToBridgeAvailability,
} from './availability';

vi.mock('@tauri-apps/api/event', () => ({
  listen: vi.fn(),
}));

describe('listenToBridgeAvailability', () => {
  beforeEach(() => vi.clearAllMocks());

  it('只订阅固定的 Bridge 状态事件并传递安全状态', async () => {
    const stop = vi.fn();
    const handler = vi.fn();
    vi.mocked(listen).mockImplementation((_name, callback) => {
      callback({
        event: bridgeAvailabilityEventName,
        id: 1,
        payload: { state: 'ready', generation: 2 },
      });
      return Promise.resolve(stop);
    });

    const unlisten = await listenToBridgeAvailability(handler);

    expect(listen).toHaveBeenCalledWith(bridgeAvailabilityEventName, expect.any(Function));
    expect(handler).toHaveBeenCalledWith({ state: 'ready', generation: 2 });
    expect(unlisten).toBe(stop);
  });
});
