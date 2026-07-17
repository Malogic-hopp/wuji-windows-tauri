import {
  formatSettingsInteger,
  readSettingsDraftString,
  settingsFieldId,
  type SettingsDraft,
  type SettingsDraftValue,
  type SettingsFieldErrors,
  type SettingsFieldName,
} from './settingsModel';

export type SettingsSaveState = 'ready' | 'saving' | 'success' | 'error';

interface SettingsViewProps {
  readonly draft: SettingsDraft;
  readonly baseline: SettingsDraft;
  readonly dirty: boolean;
  readonly saveState: SettingsSaveState;
  readonly fieldErrors: SettingsFieldErrors;
  readonly errorMessage?: string;
  readonly onChange: (field: SettingsFieldName, value: SettingsDraftValue) => void;
  readonly onSave: () => void;
  readonly onRetrySave: () => void;
  readonly onRestoreDefaults: () => void;
  readonly onDiscard: () => void;
  readonly locale?: string;
}

const agentNumberFields: ReadonlyArray<{
  field: SettingsFieldName;
  label: string;
  description: string;
  unit: string;
}> = [
  { field: 'agentOptions.samplingIntervalSeconds', label: '采样间隔', description: 'Agent 获取前台活动的节奏。', unit: '秒' },
  { field: 'agentOptions.idleThresholdSeconds', label: '空闲阈值', description: '超过该时间后将活动标记为空闲。', unit: '秒' },
  { field: 'agentOptions.heartbeatIntervalSeconds', label: '心跳间隔', description: 'Agent 写入运行心跳的频率。', unit: '秒' },
  { field: 'agentOptions.staleThresholdSeconds', label: '过期阈值', description: '超过该时间未收到心跳时显示为异常。', unit: '秒' },
  { field: 'agentOptions.retentionDays', label: '数据保留时间', description: 'Application 将验证允许的保留范围。', unit: '天' },
];

export function SettingsView({
  draft,
  baseline,
  dirty,
  saveState,
  fieldErrors,
  errorMessage,
  onChange,
  onSave,
  onRetrySave,
  onRestoreDefaults,
  onDiscard,
  locale,
}: SettingsViewProps) {
  const saving = saveState === 'saving';

  return (
    <div className="page settings-page page-enter">
      <header className="page-header settings-header">
        <div>
          <p className="eyebrow">本地设置</p>
          <h1 tabIndex={-1}>设置</h1>
          <p>只显示经过批准的安全字段；默认值、范围与保存合并仍由 Core 和 Application 决定。</p>
        </div>
        <span className={`settings-dirty-badge${dirty ? ' settings-dirty-badge--active' : ''}`}>
          {dirty ? '有未保存修改' : '已与本地设置同步'}
        </span>
      </header>

      {saveState === 'success' && (
        <div className="settings-feedback settings-feedback--success" role="status" aria-live="polite">
          设置已保存，并已重新读取本地结果。
        </div>
      )}
      {saveState === 'error' && (
        <div className="settings-feedback settings-feedback--error" role="alert">
          <span className="settings-feedback__symbol" aria-hidden="true">!</span>
          <div><strong>无法完成设置操作</strong><p>{errorMessage ?? '请检查标记的字段后重试。'}</p></div>
        </div>
      )}

      <form className="settings-form" noValidate onSubmit={(event) => { event.preventDefault(); onSave(); }}>
        <fieldset disabled={saving} aria-describedby="settings-form-help">
          <legend className="sr-only">可编辑设置</legend>
          <p id="settings-form-help" className="sr-only">所有修改都需要点击保存后才会写入本地设置。</p>

          <section className="settings-section" aria-labelledby="settings-appearance-title">
            <SectionHeading id="settings-appearance-title" title="外观与刷新" description="控制 UI 呈现和本地查询刷新节奏。" />
            <div className="settings-fields settings-fields--two-columns">
              <div className="settings-field">
                <label className="settings-field__label" htmlFor={settingsFieldId('appSettings.theme')}>主题</label>
                <span id="settings-theme-description" className="settings-field__description">选择浅色、深色或 Windows 高对比度呈现。</span>
                <select
                  id={settingsFieldId('appSettings.theme')}
                  aria-describedby="settings-theme-description"
                  value={draft.appSettings.theme}
                  onChange={(event) => onChange('appSettings.theme', event.target.value)}
                >
                  <option value="light">浅色</option>
                  <option value="dark">深色</option>
                  <option value="high_contrast">高对比度</option>
                </select>
              </div>
              <NumberField
                field="appSettings.refreshIntervalSeconds"
                label="界面刷新间隔"
                description="Dashboard 主动刷新本地摘要的节奏。"
                unit="秒"
                value={draft.appSettings.refreshIntervalSeconds}
                savedValue={baseline.appSettings.refreshIntervalSeconds}
                error={fieldErrors['appSettings.refreshIntervalSeconds']}
                locale={locale}
                onChange={onChange}
              />
            </div>
          </section>

          <section className="settings-section" aria-labelledby="settings-app-title">
            <SectionHeading id="settings-app-title" title="应用行为" description="只影响 UI 与 Agent 的安全启动协作。" />
            <ToggleField
              field="appSettings.autoStartAgentWhenAppStarts"
              label="打开应用时自动启动 Agent"
              description="仅在 Agent 尚未运行时请求启动；关闭 UI 不会停止 Agent。"
              checked={draft.appSettings.autoStartAgentWhenAppStarts}
              onChange={onChange}
            />
          </section>

          <section className="settings-section" aria-labelledby="settings-agent-title">
            <SectionHeading id="settings-agent-title" title="Agent 采集参数" description="前端只检查整数格式，业务范围和字段关系由 Application 校验。" />
            <div className="settings-fields settings-fields--two-columns">
              {agentNumberFields.map((item) => (
                <NumberField
                  key={item.field}
                  {...item}
                  value={readSettingsDraftString(draft, item.field)}
                  savedValue={readSettingsDraftString(baseline, item.field)}
                  error={fieldErrors[item.field]}
                  locale={locale}
                  onChange={onChange}
                />
              ))}
            </div>
          </section>

          <section className="settings-section" aria-labelledby="settings-recording-title">
            <SectionHeading id="settings-recording-title" title="记录与隐私" description="控制安全日志、会话合并和标题遮罩。" />
            <div className="settings-toggles">
              <ToggleField field="agentOptions.enableJsonlJournal" label="启用 JSONL 活动日志" description="保留结构化本地运行记录。" checked={draft.agentOptions.enableJsonlJournal} onChange={onChange} />
              <ToggleField field="agentOptions.enableAgentEventJournal" label="启用 Agent 事件日志" description="记录启动、暂停、恢复和停止等安全事件。" checked={draft.agentOptions.enableAgentEventJournal} onChange={onChange} />
              <ToggleField field="agentOptions.enableSessionMerge" label="合并连续会话" description="由 Application 和 Agent 使用既有会话合并规则。" checked={draft.agentOptions.enableSessionMerge} onChange={onChange} />
              <ToggleField field="agentOptions.maskWindowTitles" label="遮罩窗口标题" description="减少活动记录中的敏感标题内容。" checked={draft.agentOptions.maskWindowTitles} onChange={onChange} />
            </div>
          </section>
        </fieldset>

        <aside className="privacy-note settings-privacy-note">
          <span className="settings-privacy-note__mark" aria-hidden="true">✓</span>
          <div><strong>本地优先</strong><p>此页面不能访问文件、注册表或 SQLite，只能调用固定的 Settings command。</p></div>
        </aside>

        <div className="settings-actions">
          <div className="settings-actions__secondary">
            <button className="button button--secondary" type="button" onClick={onRestoreDefaults} disabled={saving}>
              恢复默认值
            </button>
            <button className="button button--secondary" type="button" onClick={onDiscard} disabled={saving || !dirty}>
              放弃修改
            </button>
          </div>
          <div className="settings-actions__primary">
            {saveState === 'error' && (
              <button className="button button--secondary" type="button" onClick={onRetrySave} disabled={saving || !dirty}>
                重试保存
              </button>
            )}
            <button className="button button--primary" type="submit" disabled={saving || !dirty} aria-disabled={saving || !dirty}>
              {saving ? '正在保存…' : '保存设置'}
            </button>
          </div>
        </div>
      </form>

      <p className="sr-only" role="status" aria-live="polite" aria-atomic="true">
        {saveState === 'saving' ? '正在保存设置，请稍候。' : dirty ? '当前有未保存的设置修改。' : ''}
      </p>
    </div>
  );
}

export function SettingsLoading() {
  return (
    <div className="page settings-page page-enter">
      <header className="page-header"><div><p className="eyebrow">本地设置</p><h1 tabIndex={-1}>设置</h1></div></header>
      <section className="settings-state" role="status" aria-live="polite" aria-busy="true">
        <span className="settings-state__spinner" aria-hidden="true" />
        <div><h2>正在读取本地设置</h2><p>正在通过安全 Bridge 获取当前值和 Core 默认值。</p></div>
      </section>
    </div>
  );
}

export function SettingsLoadError({ message, retrying, onRetry }: { readonly message: string; readonly retrying: boolean; readonly onRetry: () => void }) {
  return (
    <div className="page settings-page page-enter">
      <header className="page-header"><div><p className="eyebrow">本地设置</p><h1 tabIndex={-1}>设置</h1></div></header>
      <section className="settings-state settings-state--error" role="alert" aria-labelledby="settings-load-error-title">
        <span className="settings-state__symbol" aria-hidden="true">!</span>
        <div><h2 id="settings-load-error-title">暂时无法读取设置</h2><p>{message}</p></div>
        <button className="button button--secondary" type="button" onClick={onRetry} disabled={retrying}>
          {retrying ? '正在重试' : '重试'}
        </button>
      </section>
    </div>
  );
}

function SectionHeading({ id, title, description }: { readonly id: string; readonly title: string; readonly description: string }) {
  return <div className="settings-section__heading"><span className="settings-section__marker" aria-hidden="true" /><div><h2 id={id}>{title}</h2><p>{description}</p></div></div>;
}

function NumberField({ field, label, description, unit, value, savedValue, error, locale, onChange }: { readonly field: SettingsFieldName; readonly label: string; readonly description: string; readonly unit: string; readonly value: string; readonly savedValue: string; readonly error?: string; readonly locale?: string; readonly onChange: (field: SettingsFieldName, value: SettingsDraftValue) => void }) {
  const id = settingsFieldId(field);
  const descriptionId = `${id}-description`;
  const errorId = `${id}-error`;
  return (
    <div className="settings-field">
      <label className="settings-field__label" htmlFor={id}>{label}</label>
      <span id={descriptionId} className="settings-field__description">{description} 当前已保存：{formatSettingsInteger(savedValue, locale)} {unit}。</span>
      <span className="settings-number-control">
        <input id={id} type="text" inputMode="numeric" pattern="-?[0-9]*" value={value} aria-invalid={error ? 'true' : undefined} aria-describedby={`${descriptionId}${error ? ` ${errorId}` : ''}`} onChange={(event) => onChange(field, event.target.value)} />
        <span aria-hidden="true">{unit}</span>
      </span>
      {error && <span id={errorId} className="settings-field__error">{error}</span>}
    </div>
  );
}

function ToggleField({ field, label, description, checked, onChange }: { readonly field: SettingsFieldName; readonly label: string; readonly description: string; readonly checked: boolean; readonly onChange: (field: SettingsFieldName, value: SettingsDraftValue) => void }) {
  const id = settingsFieldId(field);
  const descriptionId = `${id}-description`;
  return (
    <label className="settings-toggle" htmlFor={id}>
      <span><strong>{label}</strong><span id={descriptionId}>{description}</span></span>
      <input id={id} type="checkbox" checked={checked} aria-describedby={descriptionId} onChange={(event) => onChange(field, event.target.checked)} />
    </label>
  );
}
