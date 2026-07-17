import { listen, type UnlistenFn } from '@tauri-apps/api/event';

export const hostCloseRequestedEvent = 'host://close-requested';

export type HostCloseIntent = 'hide' | 'exit';

interface HostCloseRequestedPayload {
  readonly intent: unknown;
}

export function subscribeHostCloseRequested(
  handler: (intent: HostCloseIntent) => void,
): Promise<UnlistenFn> {
  return listen<HostCloseRequestedPayload>(hostCloseRequestedEvent, (event) => {
    if (event.payload.intent === 'hide' || event.payload.intent === 'exit') {
      handler(event.payload.intent);
    }
  });
}
