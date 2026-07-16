import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  listenToBridgeAvailability,
  type BridgeAvailabilityState,
} from '../../bridge/availability';
import { bridgeClient, toCommandError, type AgentCommand } from '../../bridge/client';
import { AgentCommandBar } from './AgentCommandBar';

const initializeKey = ['app', 'initialize'] as const;
const agentStatusKey = ['agent', 'status'] as const;

export function AgentCommandContainer() {
  const queryClient = useQueryClient();
  const [availability, setAvailability] = useState<BridgeAvailabilityState>();

  useEffect(() => {
    let disposed = false;
    let unlisten: (() => void) | undefined;

    void listenToBridgeAvailability((event) => {
      setAvailability(event.state);
      if (event.state === 'ready') {
        void Promise.all([
          queryClient.resetQueries({ queryKey: initializeKey }),
          queryClient.resetQueries({ queryKey: agentStatusKey }),
        ]);
      }
    }).then((stopListening) => {
      if (disposed) {
        stopListening();
      } else {
        unlisten = stopListening;
      }
    });

    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [queryClient]);
  const initialize = useQuery({
    queryKey: initializeKey,
    queryFn: bridgeClient.initialize,
  });
  const status = useQuery({
    queryKey: agentStatusKey,
    queryFn: bridgeClient.getAgentStatus,
    enabled: initialize.isSuccess,
    refetchInterval: 4_000,
  });
  const command = useMutation({
    mutationFn: (value: AgentCommand) => bridgeClient.runAgentCommand(value),
    onSuccess: async () => queryClient.invalidateQueries({ queryKey: agentStatusKey }),
  });
  const retry = useMutation({
    mutationFn: bridgeClient.retry,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: initializeKey });
      await queryClient.invalidateQueries({ queryKey: agentStatusKey });
    },
  });

  const disconnected = availability === 'unavailable'
    || availability === 'circuit_open'
    || initialize.isError
    || status.isError
    || retry.isError;

  return (
    <AgentCommandBar
      status={status.data}
      busy={command.isPending || retry.isPending}
      disconnected={disconnected}
      commandError={command.isError ? toCommandError(command.error).message : undefined}
      onCommand={(value) => {
        command.reset();
        command.mutate(value);
      }}
      onRetry={() => {
        command.reset();
        retry.reset();
        retry.mutate();
      }}
    />
  );
}
