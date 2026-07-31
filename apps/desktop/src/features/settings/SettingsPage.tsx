import { useCallback, useEffect, useState } from 'react';
import type { SettingsDto } from '../../types/wuji-core';
import {
  bridgeClient,
  toSafeError,
  type SafeError,
} from '../../bridge/client';
import { PageStateView, type PagePhase } from '../../components/PageState';

const SAMPLING_OPTIONS = [1, 3, 5, 10] as const;

type SettingsModel =
  | { phase: 'loading' }
  | { phase: 'ready'; dto: SettingsDto }
  | { phase: 'error'; error: SafeError };

type SaveOutcome =
  | { kind: 'none' }
  | { kind: 'saved'; message: string }
  | { kind: 'warn'; message: string }
  | { kind: 'error'; error: SafeError };

/** 设置（09 §10.4）：六个字段；保存成功与 Agent 已应用分开显示。 */
export default function SettingsPage() {
  const [model, setModel] = useState<SettingsModel>({ phase: 'loading' });
  const [outcome, setOutcome] = useState<SaveOutcome>({ kind: 'none' });
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    try {
      const dto = await bridgeClient.settingsGet();
      setModel({ phase: 'ready', dto });
    } catch (cause) {
      setModel({ phase: 'error', error: toSafeError(cause) });
    }
  }, []);

  useEffect(() => {
    const timer = setTimeout(() => {
      void load();
    }, 0);
    return () => {
      clearTimeout(timer);
    };
  }, [load]);

  const save = useCallback(
    async (patch: {
      samplingIntervalSeconds: number;
      idleThresholdSeconds: number;
      workBreakIdleSeconds: number;
      excludedProcessNames: string[];
      startCaptureOnLogin: boolean;
      expectedRevision: string;
    }) => {
      setSaving(true);
      setOutcome({ kind: 'none' });
      try {
        const dto = await bridgeClient.settingsUpdate(patch);
        setModel({ phase: 'ready', dto });
        setOutcome({
          kind: 'saved',
          message: `已保存并应用（revision ${dto.revision}）`,
        });
      } catch (cause) {
        const error = toSafeError(cause);
        if (error.code === 'SETTINGS_CONFLICT') {
          await load();
          setOutcome({
            kind: 'error',
            error: { code: error.code, message: '设置已被其他操作修改，已为你刷新最新值，请重试。' },
          });
        } else if (error.code === 'SETTINGS_SAVED_NOT_APPLIED') {
          await load();
          setOutcome({ kind: 'warn', message: '设置已保存，Agent 将在下次连接时应用。' });
        } else {
          setOutcome({ kind: 'error', error });
        }
      } finally {
        setSaving(false);
      }
    },
    [load],
  );

  const phase: PagePhase =
    model.phase === 'loading'
      ? { kind: 'loading' }
      : model.phase === 'error'
        ? { kind: 'error', error: model.error, onRetry: () => void load() }
        : { kind: 'ready' };

  return (
    <div className="page">
      <h1 className="page__title">设置</h1>
      <PageStateView phase={phase}>
        {model.phase === 'ready' && (
          <SettingsForm
            key={`${model.dto.revision}:${model.dto.appliedRevision}`}
            dto={model.dto}
            saving={saving}
            outcome={outcome}
            onSave={save}
          />
        )}
      </PageStateView>
    </div>
  );
}

function SettingsForm({
  dto,
  saving,
  outcome,
  onSave,
}: {
  dto: SettingsDto;
  saving: boolean;
  outcome: SaveOutcome;
  onSave: (patch: {
    samplingIntervalSeconds: number;
    idleThresholdSeconds: number;
    workBreakIdleSeconds: number;
    excludedProcessNames: string[];
    startCaptureOnLogin: boolean;
    expectedRevision: string;
  }) => Promise<void>;
}) {
  const [sampling, setSampling] = useState(dto.samplingIntervalSeconds);
  const [idle, setIdle] = useState(String(dto.idleThresholdSeconds));
  const [workBreak, setWorkBreak] = useState(String(dto.workBreakIdleSeconds));
  const [excluded, setExcluded] = useState(dto.excludedProcessNames.join('\n'));
  const [onLogin, setOnLogin] = useState(dto.startCaptureOnLogin);
  const [fieldError, setFieldError] = useState<string | null>(null);

  const submit = async () => {
    const idleSeconds = Number(idle);
    const breakSeconds = Number(workBreak);
    if (!Number.isInteger(idleSeconds) || idleSeconds < 30 || idleSeconds > 1800) {
      setFieldError('空闲阈值必须是 30 到 1800 之间的整数秒');
      return;
    }
    if (!Number.isInteger(breakSeconds) || breakSeconds < 60 || breakSeconds > 3600) {
      setFieldError('工作块打断阈值必须是 60 到 3600 之间的整数秒');
      return;
    }
    if (breakSeconds <= idleSeconds) {
      setFieldError('工作块打断阈值必须大于空闲阈值');
      return;
    }
    setFieldError(null);
    await onSave({
      samplingIntervalSeconds: sampling,
      idleThresholdSeconds: idleSeconds,
      workBreakIdleSeconds: breakSeconds,
      excludedProcessNames: excluded
        .split('\n')
        .map((line) => line.trim().toLowerCase())
        .filter((line) => line.length > 0),
      startCaptureOnLogin: onLogin,
      expectedRevision: dto.revision,
    });
  };

  return (
    <div className="card">
      <div className="metric-grid" style={{ marginBottom: 16 }}>
        <div className="metric">
          <span className="metric__label">已保存 revision</span>
          <span className="metric__value">{dto.revision}</span>
        </div>
        <div className="metric">
          <span className="metric__label">Agent 已应用 revision</span>
          <span className="metric__value">{dto.appliedRevision}</span>
        </div>
      </div>
      {dto.revision !== dto.appliedRevision && (
        <div className="notice notice--warn" role="note">
          保存与 Agent 应用不一致：Agent 仍在使用 revision {dto.appliedRevision}。
        </div>
      )}
      <form
        className="form"
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <div className="form__row">
          <label className="form__label" htmlFor="sampling">
            采样间隔
          </label>
          <select
            id="sampling"
            className="form__select"
            value={sampling}
            onChange={(event) => { setSampling(Number(event.target.value)); }}
          >
            {SAMPLING_OPTIONS.map((value) => (
              <option key={value} value={value}>
                {value} 秒
              </option>
            ))}
          </select>
        </div>
        <div className="form__row">
          <label className="form__label" htmlFor="idle">
            空闲阈值（秒）
          </label>
          <input
            id="idle"
            className="form__input"
            inputMode="numeric"
            value={idle}
            onChange={(event) => { setIdle(event.target.value); }}
          />
          <span className="form__hint">无输入超过该时长记为空闲（30–1800）。</span>
        </div>
        <div className="form__row">
          <label className="form__label" htmlFor="workBreak">
            工作块打断阈值（秒）
          </label>
          <input
            id="workBreak"
            className="form__input"
            inputMode="numeric"
            value={workBreak}
            onChange={(event) => { setWorkBreak(event.target.value); }}
          />
          <span className="form__hint">
            空闲超过该时长结束当前工作块，必须大于空闲阈值（60–3600）。
          </span>
        </div>
        <div className="form__row">
          <label className="form__label" htmlFor="excluded">
            排除的应用（每行一个进程名）
          </label>
          <textarea
            id="excluded"
            className="form__textarea"
            value={excluded}
            onChange={(event) => { setExcluded(event.target.value); }}
            placeholder={'例如：\nkeepass.exe\n1password.exe'}
          />
          <span className="form__hint">
            命中的进程不记录任何内容，只标记为隐私排除时段。
          </span>
        </div>
        <label className="form__checkbox-row" htmlFor="onLogin">
          <input
            id="onLogin"
            type="checkbox"
            checked={onLogin}
            onChange={(event) => { setOnLogin(event.target.checked); }}
          />
          登录 Windows 后开始记录
        </label>
        {fieldError != null && (
          <div className="notice notice--error" role="alert">
            {fieldError}
          </div>
        )}
        {outcome.kind === 'saved' && (
          <div className="notice notice--ok" role="status">
            {outcome.message}
          </div>
        )}
        {outcome.kind === 'warn' && (
          <div className="notice notice--warn" role="status">
            {outcome.message}
          </div>
        )}
        {outcome.kind === 'error' && (
          <div className="notice notice--error" role="alert">
            {outcome.error.message}
          </div>
        )}
        <div>
          <button className="button button--primary" type="submit" disabled={saving}>
            {saving ? '正在保存…' : '保存'}
          </button>
        </div>
      </form>
    </div>
  );
}
