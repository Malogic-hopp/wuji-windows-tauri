import { useCallback, useState } from 'react';
import type { DiagnosticsDto } from '../../bridge/client';
import { bridgeClient, toSafeError, type SafeError } from '../../bridge/client';
import { PageStateView, type PagePhase } from '../../components/PageState';
import { formatAge, formatDateTime } from '../../lib/format';
import { useDocumentVisible, usePolling } from '../../lib/polling';

type DiagnosticsModel =
  | { phase: 'loading' }
  | { phase: 'ready'; dto: DiagnosticsDto; atMs: number }
  | { phase: 'error'; error: SafeError };

/** 诊断（09 §10.4）：普通语言健康状态在前，高级信息默认折叠且路径脱敏。 */
export default function DiagnosticsPage() {
  const [model, setModel] = useState<DiagnosticsModel>({ phase: 'loading' });
  const [resyncResult, setResyncResult] = useState<string | null>(null);
  const visible = useDocumentVisible();

  const refresh = useCallback(async () => {
    try {
      const dto = await bridgeClient.diagnosticsGetSummary();
      // 时间基准随每次轮询更新（审核 R09）：相对年龄不能冻结在首次渲染。
      setModel({ phase: 'ready', dto, atMs: Date.now() });
    } catch (cause) {
      setModel({ phase: 'error', error: toSafeError(cause) });
    }
  }, []);

  usePolling(refresh, 2000, visible);

  const resync = useCallback(async () => {
    setResyncResult(null);
    try {
      await bridgeClient.settingsResyncLoginStartup();
      setResyncResult('已按当前设置重新同步登录启动。');
    } catch (cause) {
      setResyncResult(toSafeError(cause).message);
    }
  }, []);

  const phase: PagePhase =
    model.phase === 'loading'
      ? { kind: 'loading' }
      : model.phase === 'error'
        ? { kind: 'error', error: model.error, onRetry: () => void refresh() }
        : model.dto.status == null && !model.dto.databaseReachable
          ? { kind: 'error', error: { code: 'DB_UNAVAILABLE', message: '无法连接 Agent，数据库也不可读。' }, onRetry: () => void refresh() }
          : { kind: 'ready' };

  return (
    <div className="page">
      <h1 className="page__title">诊断</h1>
      <PageStateView phase={phase}>
        {model.phase === 'ready' && (
          <DiagnosticsView
            dto={model.dto}
            nowMs={model.atMs}
            resyncResult={resyncResult}
            onResync={() => void resync()}
          />
        )}
      </PageStateView>
    </div>
  );
}

function DiagnosticsView({
  dto,
  nowMs,
  resyncResult,
  onResync,
}: {
  dto: DiagnosticsDto;
  nowMs: number;
  resyncResult: string | null;
  onResync: () => void;
}) {
  const status = dto.status;
  const now = nowMs;
  const connected = status != null;
  return (
    <>
      <div className="card">
        <h2 className="card__title">运行健康</h2>
        <div className="metric-grid">
          <Metric
            label="Agent 连接"
            value={connected ? '已连接' : '未连接'}
            tone={connected ? 'ok' : 'error'}
          />
          <Metric label="采集状态" value={captureLabel(status?.captureState)} />
          <Metric label="Writer 状态" value={writerLabel(status?.writerState)} />
          <Metric
            label="数据库"
            value={dto.databaseReachable ? '可读' : '不可读'}
            tone={dto.databaseReachable ? 'ok' : 'error'}
          />
        </div>
      </div>
      <div className="card">
        <h2 className="card__title">最后活动</h2>
        <div className="metric-grid">
          <Metric
            label="最后心跳"
            value={
              status?.heartbeatAtUtcMs != null
                ? formatAge(status.heartbeatAtUtcMs, now)
                : '—'
            }
          />
          <Metric
            label="最后采集"
            value={
              status?.lastObservationAtUtcMs != null
                ? formatAge(status.lastObservationAtUtcMs, now)
                : '—'
            }
          />
          <Metric
            label="最后写入"
            value={
              status?.lastWriteAtUtcMs != null
                ? formatAge(status.lastWriteAtUtcMs, now)
                : '—'
            }
          />
          <Metric
            label="队列深度"
            value={`采集 ${String(status?.captureQueueDepth ?? 0)} · 写入 ${String(status?.writerQueueDepth ?? 0)}`}
          />
          <Metric
            label="丢弃计数"
            value={`采集 ${status?.droppedCaptureCount ?? '0'} · 写入 ${status?.droppedWriterCount ?? '0'}`}
          />
          <Metric label="安全错误码" value={status?.safeErrorCode ?? '无'} />
        </div>
      </div>
      <div className="card">
        <h2 className="card__title">修复操作</h2>
        <button className="button" type="button" onClick={onResync}>
          按当前设置重新同步登录启动
        </button>
        {resyncResult != null && (
          <p className="text-dim" role="status">
            {resyncResult}
          </p>
        )}
      </div>
      <details className="details">
        <summary>高级信息（默认折叠，路径已脱敏）</summary>
        <ul className="list" style={{ marginTop: 12 }}>
          <li className="list__row">
            <div className="list__main">
              <span className="list__title">数据目录</span>
              <div className="list__sub mono">{dto.dataRootMasked}</div>
            </div>
          </li>
          <li className="list__row">
            <div className="list__main">
              <span className="list__title">Agent 程序</span>
              <div className="list__sub mono">{dto.agentExeMasked}</div>
            </div>
          </li>
          <li className="list__row">
            <div className="list__main">
              <span className="list__title">报告时区</span>
              <div className="list__sub mono">{dto.reportingTimeZoneId ?? '—'}</div>
            </div>
          </li>
          <li className="list__row">
            <div className="list__main">
              <span className="list__title">Agent 已应用设置 revision</span>
              <div className="list__sub mono">{dto.appliedRevision}</div>
            </div>
          </li>
          {status != null && (
            <li className="list__row">
              <div className="list__main">
                <span className="list__title">版本</span>
                <div className="list__sub mono">
                  Agent {status.agentVersion || '—'} · 协议 {status.protocolVersion} · Schema{' '}
                  {status.schemaVersion} · runtime {status.runtimeId}
                </div>
              </div>
            </li>
          )}
          {status?.heartbeatAtUtcMs != null && dto.reportingTimeZoneId != null && (
            <li className="list__row">
              <div className="list__main">
                <span className="list__title">最后心跳（绝对时间）</span>
                <div className="list__sub mono">
                  {formatDateTime(status.heartbeatAtUtcMs, dto.reportingTimeZoneId)}
                </div>
              </div>
            </li>
          )}
        </ul>
      </details>
    </>
  );
}

function Metric({ label, value, tone }: { label: string; value: string; tone?: 'ok' | 'error' }) {
  return (
    <div className="metric">
      <span className="metric__label">{label}</span>
      <span
        className="metric__value"
        style={tone === 'error' ? { color: 'var(--error)' } : tone === 'ok' ? { color: 'var(--ok)' } : undefined}
      >
        {value}
      </span>
    </div>
  );
}

function captureLabel(state?: 'stopped' | 'running' | 'paused'): string {
  switch (state) {
    case 'running':
      return '正在记录';
    case 'paused':
      return '已暂停';
    case 'stopped':
      return '已停止';
    default:
      return '—';
  }
}

function writerLabel(state?: 'healthy' | 'degraded' | 'faulted'): string {
  switch (state) {
    case 'healthy':
      return '正常';
    case 'degraded':
      return '降级';
    case 'faulted':
      return '故障';
    default:
      return '—';
  }
}
