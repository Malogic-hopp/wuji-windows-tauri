import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AgentCommandBar } from './AgentCommandBar';

describe('AgentCommandBar', () => {
  it('只启用运行状态允许的操作', () => {
    render(
      <AgentCommandBar
        status={{ actualState: 'running', isRunning: true, isHealthy: true, isStale: false }}
        busy={false}
        disconnected={false}
        onCommand={vi.fn()}
        onRetry={vi.fn()}
      />,
    );

    expect(screen.getByRole('button', { name: '暂停' })).toBeEnabled();
    expect(screen.getByRole('button', { name: '停止' })).toBeEnabled();
    expect(screen.getByRole('button', { name: '启动' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '继续' })).toBeDisabled();
  });

  it('断线时说明 Agent 保持运行并提供恢复操作', () => {
    const retry = vi.fn();
    render(
      <AgentCommandBar
        busy={false}
        disconnected
        onCommand={vi.fn()}
        onRetry={retry}
      />,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('Agent 会保持原状态运行');
    fireEvent.click(screen.getByRole('button', { name: '重新连接' }));
    expect(retry).toHaveBeenCalledOnce();
  });

  it('命令失败显示安全提示但不谎报 Bridge 断线', () => {
    render(
      <AgentCommandBar
        status={{ actualState: 'paused', isRunning: true, isHealthy: true, isStale: false }}
        busy={false}
        disconnected={false}
        commandError="暂停命令未完成，请刷新状态后重试。"
        onCommand={vi.fn()}
        onRetry={vi.fn()}
      />,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('暂停命令未完成');
    expect(screen.queryByText('连接已断开')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: '继续' })).toBeEnabled();
  });
});
