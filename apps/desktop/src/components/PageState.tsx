import type { ReactNode } from 'react';
import type { SafeError } from '../bridge/client';

export type PagePhase =
  | { kind: 'loading' }
  | { kind: 'empty'; title: string; hint?: string }
  | { kind: 'error'; error: SafeError; onRetry?: () => void }
  | { kind: 'ready' };

/** 四态统一展示（09 §10：Loading | Empty | Ready | Error）。 */
export function PageStateView({
  phase,
  children,
}: {
  phase: PagePhase;
  children: ReactNode;
}) {
  if (phase.kind === 'ready') {
    return <>{children}</>;
  }
  if (phase.kind === 'loading') {
    return (
      <div className="state-block" role="status">
        <div className="state-block__title">正在加载…</div>
      </div>
    );
  }
  if (phase.kind === 'empty') {
    return (
      <div className="state-block">
        <div className="state-block__title">{phase.title}</div>
        {phase.hint != null && <div>{phase.hint}</div>}
      </div>
    );
  }
  return (
    <div className="state-block" role="alert">
      <div className="state-block__title">加载失败</div>
      <div>{phase.error.message}</div>
      {phase.onRetry != null && (
        <button className="button" type="button" onClick={phase.onRetry}>
          重试
        </button>
      )}
    </div>
  );
}
