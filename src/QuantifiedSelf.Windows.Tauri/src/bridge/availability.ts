import { listen, type UnlistenFn } from '@tauri-apps/api/event';

export type BridgeAvailabilityState =
  | 'starting'
  | 'ready'
  | 'unavailable'
  | 'circuit_open'
  | 'shutting_down';

export interface BridgeAvailabilityEvent {
  readonly state: BridgeAvailabilityState;
  readonly generation: number;
}

export const bridgeAvailabilityEventName = 'bridge://availability';

export function listenToBridgeAvailability(
  handler: (event: BridgeAvailabilityEvent) => void,
): Promise<UnlistenFn> {
  return listen<BridgeAvailabilityEvent>(bridgeAvailabilityEventName, ({ payload }) => {
    handler(payload);
  });
}
