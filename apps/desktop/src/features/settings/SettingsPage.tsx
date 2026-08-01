import { useCallback, useEffect, useState } from 'react';
import type { SettingsDto } from '../../types/wuji-core';
import {
  bridgeClient,
  toSafeError,
  type DesktopPrefsDto,
  type SafeError,
} from '../../bridge/client';
import { PageStateView, type PagePhase } from '../../components/PageState';

const SAMPLING_OPTIONS = [1, 3, 5, 10] as const;

type SettingsModel =
  | { phase: 'loading' }
  | { phase: 'ready'; dto: SettingsDto; prefs: DesktopPrefsDto; prefsError: SafeError | null }
  | { phase: 'error'; error: SafeError };

type SaveOutcome =
  | { kind: 'none' }
  | { kind: 'saved'; message: string }
  | { kind: 'warn'; message: string }
  | { kind: 'error'; error: SafeError };

/** 设置（09 §10.4）：五个 Settings 字段 + Desktop 本地偏好（09 §9.4）；
 *  保存成功与 Agent 已应用分开显示。 */
export default function SettingsPage() {
  const [model, setModel] = useState<SettingsModel>({ phase: 'loading' });
  const [outcome, setOutcome] = useState<SaveOutcome>({ kind: 'none' });
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    try {
      // 偏好损坏不应挡住 Settings 页：分别请求，prefsError 以警告呈现并默认 true。
      const [dto, prefsResult] = await Promise.all([
        bridgeClient.settingsGet(),
        bridgeClient.desktopPrefsGet().then(
          (prefs) => ({ ok: true as const, prefs }),
          (cause: unknown) => ({ ok: false as const, error: toSafeError(cause) }),
        ),
      ]);
      if (prefsResult.ok) {
        setModel({ phase: 'ready', dto, prefs: prefsResult.prefs, prefsError: null });
      } else {
        setModel({
          phase: 'ready',
          dto,
          prefs: { autoStartRecordingWhenAppStarts: true },
          prefsError: prefsResult.error,
        });
      }
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
    async (
      patch: {
        samplingIntervalSeconds: number;
        idleThresholdSeconds: number;
        workBreakIdleSeconds: number;
        excludedProcessNames: string[];
        startCaptureOnLogin: boolean;
        expectedRevision: string;
      },
      prefsPatch: { autoStartRecordingWhenAppStarts: boolean },
      settingsDirty: boolean,
      prefsDirty: boolean,
    ) => {
      setSaving(true);
      setOutcome({ kind: 'none' });
      try {
        if (!settingsDirty && !prefsDirty) {
          setOutcome({ kind: 'saved', message: '没有需要保存的变更' });
          return;
        }
        // 两个保存互相独立（09 §9.4）：只改偏好不得推进 Settings revision、
        // 不得触发 Barrier/effectivity；Settings 失败不阻断偏好保存，偏好
        // 失败不掩盖 Settings 成功。
        let dto: SettingsDto | null = null;
        let prefs: DesktopPrefsDto | null = null;
        let settingsError: SafeError | null = null;
        let prefsError: SafeError | null = null;
        if (settingsDirty) {
          try {
            dto = await bridgeClient.settingsUpdate(patch);
          } catch (cause) {
            settingsError = toSafeError(cause);
          }
        }
        if (prefsDirty) {
          try {
            prefs = await bridgeClient.desktopPrefsUpdate(prefsPatch);
          } catch (cause) {
            prefsError = toSafeError(cause);
          }
        }
        if (settingsError != null && settingsError.code === 'SETTINGS_CONFLICT') {
          await load();
          setOutcome({
            kind: 'error',
            error: {
              code: settingsError.code,
              message: `设置已被其他操作修改，已为你刷新最新值，请重试${prefsError != null ? `；本地偏好保存失败：${prefsError.message}` : ''}。`,
            },
          });
          return;
        }
        if (settingsError != null && settingsError.code === 'SETTINGS_SAVED_NOT_APPLIED') {
          await load();
          setOutcome({
            kind: 'warn',
            message: `设置已保存，Agent 将在下次连接时应用${prefsError != null ? `；本地偏好保存失败：${prefsError.message}` : ''}。`,
          });
          return;
        }
        // 保存成功的字段替换模型；偏好成功写入代表损坏文件已自愈，必须
        // 显式清除旧 prefsError。未提交偏好时才保留已有警告。
        setModel((prev) => {
          if (prev.phase !== 'ready') {
            return prev;
          }
          return {
            phase: 'ready',
            dto: dto ?? prev.dto,
            prefs: prefs ?? prev.prefs,
            prefsError: prefs != null ? null : prefsError ?? prev.prefsError,
          };
        });
        if (settingsError != null) {
          setOutcome({
            kind: 'error',
            error: {
              code: settingsError.code,
              message: `${settingsError.message}${prefs != null ? '；本地偏好已保存。' : ''}`,
            },
          });
          return;
        }
        if (prefsError != null) {
          setOutcome(
            dto != null
              ? {
                  kind: 'warn',
                  message: `设置已保存并应用（revision ${dto.revision}），但本地偏好保存失败：${prefsError.message}`,
                }
              : { kind: 'error', error: prefsError },
          );
          return;
        }
        setOutcome(
          dto != null
            ? { kind: 'saved', message: `已保存并应用（revision ${dto.revision}）` }
            : { kind: 'saved', message: '已保存本地偏好' },
        );
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
            prefs={model.prefs}
            prefsError={model.prefsError}
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
  prefs,
  prefsError,
  saving,
  outcome,
  onSave,
}: {
  dto: SettingsDto;
  prefs: DesktopPrefsDto;
  prefsError: SafeError | null;
  saving: boolean;
  outcome: SaveOutcome;
  onSave: (
    patch: {
      samplingIntervalSeconds: number;
      idleThresholdSeconds: number;
      workBreakIdleSeconds: number;
      excludedProcessNames: string[];
      startCaptureOnLogin: boolean;
      expectedRevision: string;
    },
    prefsPatch: { autoStartRecordingWhenAppStarts: boolean },
    settingsDirty: boolean,
    prefsDirty: boolean,
  ) => Promise<void>;
}) {
  const [sampling, setSampling] = useState(dto.samplingIntervalSeconds);
  const [idle, setIdle] = useState(String(dto.idleThresholdSeconds));
  const [workBreak, setWorkBreak] = useState(String(dto.workBreakIdleSeconds));
  const [excluded, setExcluded] = useState(dto.excludedProcessNames.join('\n'));
  const [onLogin, setOnLogin] = useState(dto.startCaptureOnLogin);
  const [autoStart, setAutoStart] = useState(prefs.autoStartRecordingWhenAppStarts);
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
    // 按 dirty 状态独立提交：只改偏好不好调用 settings_update（不推进
    // Settings revision、不触发 Barrier/effectivity）；只改 Settings 不调用
    // desktop_prefs_update。两个 dirty 都为假时无变更，不调用任何保存。
    const settingsDirty =
      sampling !== dto.samplingIntervalSeconds ||
      idle !== String(dto.idleThresholdSeconds) ||
      workBreak !== String(dto.workBreakIdleSeconds) ||
      excluded !== dto.excludedProcessNames.join('\n') ||
      onLogin !== dto.startCaptureOnLogin;
    const prefsDirty = autoStart !== prefs.autoStartRecordingWhenAppStarts;
    await onSave(
      {
        samplingIntervalSeconds: sampling,
        idleThresholdSeconds: idleSeconds,
        workBreakIdleSeconds: breakSeconds,
        excludedProcessNames: excluded
          .split('\n')
          .map((line) => line.trim().toLowerCase())
          .filter((line) => line.length > 0),
        startCaptureOnLogin: onLogin,
        expectedRevision: dto.revision,
      },
      { autoStartRecordingWhenAppStarts: autoStart },
      settingsDirty,
      prefsDirty,
    );
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
      {prefsError != null && (
        <div className="notice notice--warn" role="note">
          {prefsError.message}。当前按默认值显示“打开 App 时自动开始记录”。
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
        <label className="form__checkbox-row" htmlFor="autoStart">
          <input
            id="autoStart"
            type="checkbox"
            checked={autoStart}
            onChange={(event) => { setAutoStart(event.target.checked); }}
          />
          启动吾迹时自动开始记录
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
