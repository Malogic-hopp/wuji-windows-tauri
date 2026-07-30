import { useCallback, useEffect, useState } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import type { AgentStatusDto } from '../types/wuji-core';
import { bridgeClient, toSafeError, type SafeError } from '../bridge/client';
import { useDocumentVisible, usePolling } from '../lib/polling';

const NAV_ITEMS: { to: string; label: string; end?: boolean }[] = [
  { to: '/', label: '今日', end: true },
  { to: '/timeline', label: '时间线' },
  { to: '/heatmap', label: '热力图' },
  { to: '/settings', label: '设置' },
  { to: '/diagnostics', label: '诊断' },
];

function captureLabel(state: AgentStatusDto['captureState']): string {
  switch (state) {
    case 'running':
      return '正在记录';
    case 'paused':
      return '已暂停';
    default:
      return '未记录';
  }
}

/** 顶栏：Agent 状态与控制（09 §10.4、AGENTS UI 规则：状态只在顶栏）。 */
export default function AppLayout() {
  const [status, setStatus] = useState<AgentStatusDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<SafeError | null>(null);
  const [theme, setTheme] = useState<'light' | 'dark' | null>(null);
  const visible = useDocumentVisible();

  useEffect(() => {
    const root = document.documentElement;
    if (theme === null) {
      root.removeAttribute('data-theme');
    } else {
      root.setAttribute('data-theme', theme);
    }
  }, [theme]);

  const refresh = useCallback(async () => {
    try {
      setStatus(await bridgeClient.agentGetStatus());
    } catch {
      setStatus(null);
    }
  }, []);

  usePolling(refresh, 2000, visible);

  const run = useCallback(
    async (command: () => Promise<AgentStatusDto>) => {
      setBusy(true);
      setError(null);
      try {
        setStatus(await command());
      } catch (cause) {
        setError(toSafeError(cause));
      } finally {
        setBusy(false);
      }
    },
    [],
  );

  const captureState = status?.captureState;
  const agentRunning = status != null && status.processState !== 'stopped';
  return (
    <div className="app-shell">
      <header className="app-topbar">
        <span className="app-topbar__brand">吾迹</span>
        <span
          className={`badge ${
            captureState === 'running'
              ? 'badge--ok'
              : captureState === 'paused'
                ? 'badge--warn'
                : 'badge--dim'
          }`}
          data-testid="capture-state-badge"
        >
          {!agentRunning ? 'Agent 未运行' : captureLabel(captureState ?? 'stopped')}
        </span>
        {status?.writerState != null && status.writerState !== 'healthy' && (
          <span className="badge badge--error">写入异常</span>
        )}
        <span className="app-topbar__spacer" />
        {(!agentRunning || captureState === 'stopped') && (
          <button
            className="button button--primary"
            type="button"
            disabled={busy}
            onClick={() => void run(bridgeClient.captureStart)}
          >
            {agentRunning ? '开始记录' : '启动并记录'}
          </button>
        )}
        {captureState === 'running' && (
          <button
            className="button"
            type="button"
            disabled={busy}
            onClick={() => void run(bridgeClient.capturePause)}
          >
            暂停
          </button>
        )}
        {captureState === 'paused' && (
          <button
            className="button button--primary"
            type="button"
            disabled={busy}
            onClick={() => void run(bridgeClient.captureResume)}
          >
            继续
          </button>
        )}
        {agentRunning && (
          <button
            className="button"
            type="button"
            disabled={busy}
            onClick={() => void run(bridgeClient.agentProcessStop)}
          >
            停止 Agent
          </button>
        )}
        <button
          className="button button--ghost"
          type="button"
          aria-label="切换主题"
          onClick={() => { setTheme((t) => (t === 'dark' ? 'light' : 'dark')); }}
        >
          {theme === 'dark' ? '浅色' : '深色'}
        </button>
      </header>
      {error != null && (
        <div className="notice notice--error" role="alert">
          {error.message}
        </div>
      )}
      <nav className="app-nav" aria-label="主导航">
        {NAV_ITEMS.map((item) => (
          <NavLink
            key={item.to}
            to={item.to}
            end={item.end ?? false}
            className="app-nav__link"
          >
            {item.label}
          </NavLink>
        ))}
      </nav>
      <main className="app-main">
        <Outlet />
      </main>
    </div>
  );
}
