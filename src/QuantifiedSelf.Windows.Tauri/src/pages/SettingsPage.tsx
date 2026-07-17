import { useEffect, useRef, useState } from 'react';
import { useBlocker } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { SettingsGetResult, SettingsSnapshot } from '../bridge/contracts';
import { bridgeClient, toCommandError } from '../bridge/client';
import {
  subscribeHostCloseRequested,
  type HostCloseIntent,
} from '../bridge/hostLifecycle';
import {
  initializeQueryKey,
  refreshQueriesAfterSettingsSaved,
  settingsQueryKey,
} from '../bridge/queryInvalidation';
import {
  SettingsLoadError,
  SettingsLoading,
  SettingsView,
  type SettingsSaveState,
} from '../features/settings/SettingsView';
import {
  createSettingsDraft,
  isSettingsDirty,
  mapBridgeFieldErrors,
  parseSettingsDraft,
  settingsFieldId,
  updateSettingsDraft,
  type SettingsDraft,
  type SettingsDraftValue,
  type SettingsFieldErrors,
  type SettingsFieldName,
} from '../features/settings/settingsModel';

export default function SettingsPage() {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<SettingsDraft>();
  const [baseline, setBaseline] = useState<SettingsDraft>();
  const [defaults, setDefaults] = useState<SettingsDraft>();
  const [fieldErrors, setFieldErrors] = useState<SettingsFieldErrors>({});
  const [saveState, setSaveState] = useState<SettingsSaveState>('ready');
  const [errorMessage, setErrorMessage] = useState<string>();
  const [nativeCloseIntent, setNativeCloseIntent] = useState<HostCloseIntent>();
  const [nativeClosePending, setNativeClosePending] = useState(false);
  const [nativeCloseError, setNativeCloseError] = useState<string>();
  const formRef = useRef({ draft, baseline });
  const lastSettingsUpdate = useRef(0);

  const initialize = useQuery({ queryKey: initializeQueryKey, queryFn: bridgeClient.initialize });
  const settings = useQuery({
    queryKey: settingsQueryKey,
    queryFn: bridgeClient.getSettings,
    enabled: initialize.isSuccess,
  });

  useEffect(() => {
    formRef.current = { draft, baseline };
  }, [draft, baseline]);

  useEffect(() => {
    if (!settings.data || settings.dataUpdatedAt === lastSettingsUpdate.current) return;
    lastSettingsUpdate.current = settings.dataUpdatedAt;
    const nextDefaults = createSettingsDraft(settings.data.defaults);
    setDefaults(nextDefaults);

    const current = formRef.current;
    if (current.draft && current.baseline && isSettingsDirty(current.draft, current.baseline)) return;
    const next = createSettingsDraft(settings.data.settings);
    setDraft(next);
    setBaseline(next);
  }, [settings.data, settings.dataUpdatedAt]);

  const save = useMutation({
    mutationFn: (snapshot: SettingsSnapshot) => bridgeClient.updateSettings(snapshot),
    onSuccess: async (result) => {
      const next = createSettingsDraft(result.settings);
      setDraft(next);
      setBaseline(next);
      setFieldErrors({});
      setErrorMessage(undefined);
      setSaveState('success');
      queryClient.setQueryData<SettingsGetResult>(settingsQueryKey, (current) => (
        current ? { ...current, settings: result.settings } : current
      ));
      await refreshQueriesAfterSettingsSaved(queryClient);
    },
    onError: (error) => {
      const commandError = toCommandError(error);
      const nextFieldErrors = mapBridgeFieldErrors(commandError.fieldErrors);
      setFieldErrors(nextFieldErrors);
      setErrorMessage(commandError.message);
      setSaveState('error');
      focusFirstError(nextFieldErrors);
    },
  });

  const dirty = Boolean(draft && baseline && isSettingsDirty(draft, baseline));
  const blocker = useBlocker(dirty);
  const blockerRef = useRef(blocker);

  useEffect(() => {
    blockerRef.current = blocker;
  }, [blocker]);

  useEffect(() => {
    void bridgeClient.setUnsavedChanges(dirty);
  }, [dirty]);

  useEffect(() => () => {
    void bridgeClient.setUnsavedChanges(false);
  }, []);

  useEffect(() => {
    let disposed = false;
    let unlisten: (() => void) | undefined;
    void subscribeHostCloseRequested((intent) => {
      if (blockerRef.current.state === 'blocked') {
        blockerRef.current.reset();
      }
      setNativeCloseError(undefined);
      setNativeCloseIntent(intent);
    }).then((removeListener) => {
      if (disposed) {
        removeListener();
      } else {
        unlisten = removeListener;
      }
    });
    return () => {
      disposed = true;
      unlisten?.();
    };
  }, []);

  useEffect(() => {
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      if (!dirty) return;
      event.preventDefault();
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [dirty]);

  const submit = () => {
    if (save.isPending || !draft || !dirty) return;
    const parsed = parseSettingsDraft(draft);
    if (!parsed.settings) {
      setFieldErrors(parsed.fieldErrors);
      setErrorMessage('请检查标记的字段后重试。');
      setSaveState('error');
      focusFirstError(parsed.fieldErrors);
      return;
    }
    setFieldErrors({});
    setErrorMessage(undefined);
    setSaveState('saving');
    save.mutate(parsed.settings);
  };

  const change = (field: SettingsFieldName, value: SettingsDraftValue) => {
    setDraft((current) => current ? updateSettingsDraft(current, field, value) : current);
    setFieldErrors((current) => {
      const { [field]: removed, ...remaining } = current;
      void removed;
      return remaining;
    });
    setErrorMessage(undefined);
    setSaveState('ready');
  };

  const retryLoad = async () => {
    if (!initialize.isSuccess) {
      const result = await initialize.refetch();
      if (result.isError) return;
    }
    await settings.refetch();
  };

  const loadError = initialize.error ?? settings.error;
  if (loadError && !draft) {
    return <SettingsLoadError message={toCommandError(loadError).message} retrying={initialize.isFetching || settings.isFetching} onRetry={() => { void retryLoad(); }} />;
  }
  if (!draft || !baseline || !defaults) {
    return <SettingsLoading />;
  }

  return (
    <>
      <SettingsView
        draft={draft}
        baseline={baseline}
        dirty={dirty}
        saveState={save.isPending ? 'saving' : settings.isError ? 'error' : saveState}
        fieldErrors={fieldErrors}
        errorMessage={errorMessage ?? (settings.error ? toCommandError(settings.error).message : undefined)}
        onChange={change}
        onSave={submit}
        onRetrySave={submit}
        onRestoreDefaults={() => {
          setDraft(defaults);
          setFieldErrors({});
          setErrorMessage(undefined);
          setSaveState('ready');
        }}
        onDiscard={() => {
          setDraft(baseline);
          setFieldErrors({});
          setErrorMessage(undefined);
          setSaveState('ready');
        }}
      />
      {blocker.state === 'blocked' && !nativeCloseIntent && (
        <UnsavedChangesDialog
          intent="navigate"
          onStay={() => blocker.reset()}
          onDiscard={() => blocker.proceed()}
        />
      )}
      {nativeCloseIntent && (
        <UnsavedChangesDialog
          intent={nativeCloseIntent}
          pending={nativeClosePending}
          errorMessage={nativeCloseError}
          onStay={() => {
            setNativeCloseIntent(undefined);
            setNativeCloseError(undefined);
            void bridgeClient.cancelClose();
          }}
          onDiscard={() => {
            setNativeClosePending(true);
            setNativeCloseError(undefined);
            const action = nativeCloseIntent === 'exit'
              ? bridgeClient.requestExit()
              : bridgeClient.hideWindow();
            void action.then(() => {
              setDraft(baseline);
              setFieldErrors({});
              setErrorMessage(undefined);
              setSaveState('ready');
              setNativeCloseIntent(undefined);
            }).catch((error: unknown) => {
              setNativeCloseError(toCommandError(error).message);
            }).finally(() => {
              setNativeClosePending(false);
            });
          }}
        />
      )}
    </>
  );
}

function UnsavedChangesDialog({
  intent,
  pending = false,
  errorMessage,
  onStay,
  onDiscard,
}: {
  readonly intent: HostCloseIntent | 'navigate';
  readonly pending?: boolean;
  readonly errorMessage?: string;
  readonly onStay: () => void;
  readonly onDiscard: () => void;
}) {
  const stayButton = useRef<HTMLButtonElement>(null);
  const discardButton = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    stayButton.current?.focus();
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onStay();
      } else if (event.key === 'Tab' && event.shiftKey && document.activeElement === stayButton.current) {
        event.preventDefault();
        discardButton.current?.focus();
      } else if (event.key === 'Tab' && !event.shiftKey && document.activeElement === discardButton.current) {
        event.preventDefault();
        stayButton.current?.focus();
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [onStay]);

  return (
    <div className="settings-dialog-backdrop">
      <section className="settings-dialog" role="alertdialog" aria-modal="true" aria-labelledby="settings-unsaved-title" aria-describedby="settings-unsaved-description">
        <h2 id="settings-unsaved-title">保留未保存的修改？</h2>
        <p id="settings-unsaved-description">{unsavedDescription(intent)}</p>
        {errorMessage && <p className="settings-dialog__error" role="alert">{errorMessage}</p>}
        <div className="settings-dialog__actions">
          <button ref={stayButton} className="button button--primary" type="button" disabled={pending} onClick={onStay}>留下继续编辑</button>
          <button ref={discardButton} className="button button--secondary" type="button" disabled={pending} onClick={onDiscard}>
            {pending ? '正在处理…' : discardLabel(intent)}
          </button>
        </div>
      </section>
    </div>
  );
}

function unsavedDescription(intent: HostCloseIntent | 'navigate') {
  if (intent === 'hide') return '关闭窗口会将吾迹隐藏到托盘，并丢弃当前修改。Agent 会继续独立运行。';
  if (intent === 'exit') return '退出吾迹会关闭当前界面并丢弃修改，但不会停止正在运行的 Agent。';
  return '离开设置页会丢弃当前修改。你可以留下继续保存，或确认放弃后离开。';
}

function discardLabel(intent: HostCloseIntent | 'navigate') {
  if (intent === 'hide') return '放弃并隐藏到托盘';
  if (intent === 'exit') return '放弃并退出界面';
  return '放弃并离开';
}

function focusFirstError(errors: SettingsFieldErrors) {
  const firstField = Object.keys(errors)[0] as SettingsFieldName | undefined;
  if (!firstField) return;
  window.requestAnimationFrame(() => document.getElementById(settingsFieldId(firstField))?.focus());
}
