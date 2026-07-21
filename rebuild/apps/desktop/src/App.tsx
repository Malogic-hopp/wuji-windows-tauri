import { useEffect, useState } from 'react';
import { invoke } from '@tauri-apps/api/core';
import type { AgentStatusDto, TodayDto } from './types/wuji-core';

/**
 * V01-6 占位壳：仅用于验证 React → Tauri → Rust Agent/SQLite 端到端链路。
 * Today、Timeline、Settings、Diagnostics 四个正式页面在 V01-7 实现。
 */
export default function App() {
  const [status, setStatus] = useState<AgentStatusDto | null>(null);
  const [today, setToday] = useState<TodayDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const refresh = async () => {
      try {
        const [statusResult, todayResult] = await Promise.all([
          invoke<AgentStatusDto>('agent_get_status'),
          invoke<TodayDto>('activity_get_today'),
        ]);
        if (!cancelled) {
          setStatus(statusResult);
          setToday(todayResult);
          setError(null);
        }
      } catch (cause) {
        if (!cancelled) {
          setError(cause instanceof Error ? cause.message : String(cause));
        }
      }
    };
    void refresh();
    const timer = setInterval(() => {
      void refresh();
    }, 5000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, []);

  const captureLabel =
    status === null
      ? '正在连接…'
      : status.captureState === 'running'
        ? '正在记录'
        : status.captureState === 'paused'
          ? '已暂停'
          : '未运行';

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', padding: 24, maxWidth: 720 }}>
      <h1>吾迹 Rebuild v0.1（开发）</h1>
      <p data-testid="capture-state">Agent：{captureLabel}</p>
      {status?.safeErrorCode != null && <p>安全错误码：{status.safeErrorCode}</p>}
      {error != null && <p role="alert">连接异常：{error}</p>}
      <section>
        <h2>今日</h2>
        {today === null ? (
          <p>暂无数据</p>
        ) : (
          <ul>
            <li>日期：{today.localDate}</li>
            <li>活跃时长（毫秒）：{today.activeDurationMs}</li>
            <li>工作块：{today.workBlockCount}</li>
            <li>应用切换：{today.rawAppSwitchCount}</li>
          </ul>
        )}
      </section>
      <p style={{ color: '#666' }}>
        这是 V01-6 占位页面；正式界面在 V01-7 提供。
      </p>
    </main>
  );
}
