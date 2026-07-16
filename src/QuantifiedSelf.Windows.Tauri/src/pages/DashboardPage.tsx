import { useQuery } from '@tanstack/react-query';
import { bridgeClient, toCommandError } from '../bridge/client';
import {
  activityOverviewQueryKey,
  initializeQueryKey,
} from '../bridge/queryInvalidation';
import { DashboardView } from '../features/dashboard/DashboardView';
import {
  getOverviewRefreshInterval,
  isOverviewEmpty,
  type DashboardViewState,
} from '../features/dashboard/dashboardModel';
import { useDocumentVisibility } from '../features/dashboard/useDocumentVisibility';

export default function DashboardPage() {
  const visible = useDocumentVisibility();
  const initialize = useQuery({
    queryKey: initializeQueryKey,
    queryFn: bridgeClient.initialize,
  });
  const overview = useQuery({
    queryKey: activityOverviewQueryKey,
    queryFn: bridgeClient.getActivityOverview,
    enabled: initialize.isSuccess,
    refetchInterval: getOverviewRefreshInterval(visible),
    refetchIntervalInBackground: true,
  });

  const error = initialize.error ?? overview.error;
  let state: DashboardViewState;
  if (error) {
    state = { kind: 'error', message: toCommandError(error).message };
  } else if (initialize.isPending || overview.isPending || overview.data === undefined) {
    state = { kind: 'loading' };
  } else if (isOverviewEmpty(overview.data)) {
    state = { kind: 'empty', updatedAt: overview.dataUpdatedAt };
  } else {
    state = { kind: 'ready', overview: overview.data, updatedAt: overview.dataUpdatedAt };
  }

  const refresh = async () => {
    if (!initialize.isSuccess) {
      const initialization = await initialize.refetch();
      if (initialization.isError) return;
    }
    await overview.refetch();
  };

  return (
    <DashboardView
      state={state}
      refreshing={initialize.isFetching || overview.isFetching}
      onRefresh={() => { void refresh(); }}
    />
  );
}
