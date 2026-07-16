import { Component, type ErrorInfo, type ReactNode } from 'react';
import { WarningCircleIcon } from '@phosphor-icons/react';

interface Props {
  readonly children: ReactNode;
}

interface State {
  readonly failed: boolean;
}

export class ErrorBoundary extends Component<Props, State> {
  public state: State = { failed: false };

  public static getDerivedStateFromError(): State {
    return { failed: true };
  }

  public componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('React render failed', { name: error.name, componentStack: info.componentStack });
  }

  public render(): ReactNode {
    if (this.state.failed) {
      return (
        <main className="fatal-state" role="alert">
          <WarningCircleIcon size={28} aria-hidden="true" />
          <h1>界面暂时无法显示</h1>
          <p>后台记录服务不会因此停止。请重新打开吾迹后再试。</p>
          <button type="button" onClick={() => window.location.reload()}>重新载入</button>
        </main>
      );
    }

    return this.props.children;
  }
}
