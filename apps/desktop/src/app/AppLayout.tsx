import { useCallback, useEffect, useRef, useState } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import type { AgentStatusDto } from '../types/wuji-core';
import { bridgeClient, toSafeError, type AutoStartDto, type SafeError } from '../bridge/client';
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

/** 顶栏：Agent 状态与控制（09 §10.5、AGENTS UI 规则：状态只在顶栏）。 */
export default function AppLayout() {
  const [status, setStatus] = useState<AgentStatusDto | null>(null);
  const [autoStart, setAutoStart] = useState<AutoStartDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<SafeError | null>(null);
  const [theme, setTheme] = useState<'light' | 'dark' | null>(null);
  const visible = useDocumentVisible();
  const autoStartGenerationRef = useRef(0);

  useEffect(() => {
    const root = document.documentElement;
    if (theme === null) {
      root.removeAttribute('data-theme');
    } else {
      root.setAttribute('data-theme', theme);
    }
  }, [theme]);

  const refreshAutoStart = useCallback(async () => {
    const generation = ++autoStartGenerationRef.current;
    const result = await bridgeClient.autoStartStatus().then(
      (value) => ({ ok: true as const, value }),
      (cause: unknown) => ({ ok: false as const, error: toSafeError(cause) }),
    );
    // 手动控制后的刷新拥有更高 generation；更早发出的轮询即使迟到，
    // 也不得把已经清除的 failed 重新覆盖回来。
    if (generation === autoStartGenerationRef.current && result.ok) {
      setAutoStart(result.value);
    }
  }, []);

  const refresh = useCallback(async () => {
    // 自动开始记录编排状态与 Agent 状态分开获取：启动期间 Agent 尚不可达时，
    // auto_start_status 仍可读（Host 侧状态），顶栏才能显示“正在开始记录…”。
    const [statusResult] = await Promise.all([
      bridgeClient.agentGetStatus().then(
        (value) => ({ ok: true as const, value }),
        (cause: unknown) => ({ ok: false as const, error: toSafeError(cause) }),
      ),
    ]);
    setStatus(statusResult.ok ? statusResult.value : null);
    await refreshAutoStart();
  }, [refreshAutoStart]);

  usePolling(refresh, 2000, visible);

  const run = useCallback(
    async (command: () => Promise<AgentStatusDto>) => {
      setBusy(true);
      setError(null);
      try {
        setStatus(await command());
        // 手动控制成功后只刷新编排状态（命令结果已是权威 Agent 状态，
        // 不得用轮询旧值覆盖）：Host 已清除 AutoStartOutcome，自动启动
        // 失败提示及时消失，不必等下个轮询周期（09 §9.3）。
        void refreshAutoStart();
      } catch (cause) {
        setError(toSafeError(cause));
      } finally {
        setBusy(false);
      }
    },
    [refreshAutoStart],
  );

  const captureState = status?.captureState;
  const agentRunning = status != null && status.processState !== 'stopped';
  // 启动编排瞬态：Agent 尚不可达时由 Host 侧 auto_start_status 提供，
  // 只有收到 Agent 确认（状态轮询显示记录中）后自然消失。
  const autoStarting = autoStart?.state === 'starting';
  return (
    <div className="app-shell">
      <header className="app-topbar">
        <span className="app-topbar__brand">吾迹</span>
        <span
          className={`badge ${
            autoStarting
              ? 'badge--warn'
              : captureState === 'running'
                ? 'badge--ok'
                : captureState === 'paused'
                  ? 'badge--warn'
                  : 'badge--dim'
          }`}
          data-testid="capture-state-badge"
        >
          {autoStarting
            ? '正在开始记录…'
            : !agentRunning
              ? 'Agent 未运行'
              : captureLabel(captureState ?? 'stopped')}
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
      {autoStart?.state === 'failed' && autoStart.error != null && (
        <div className="notice notice--error" role="alert">
          自动开始记录失败：{autoStart.error.message}
        </div>
      )}
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
