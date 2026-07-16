import { GearSixIcon, ShieldCheckIcon } from '@phosphor-icons/react';

export default function SettingsPage() {
  return (
    <div className="page page-enter">
      <header className="page-header">
        <div>
          <p className="eyebrow">本地设置</p>
          <h1 tabIndex={-1}>设置</h1>
          <p>设置仍由 Core 与 Application 校验；前端只呈现经过批准的字段。</p>
        </div>
      </header>
      <div className="settings-grid">
        <section className="placeholder-card" aria-labelledby="settings-placeholder-title">
          <div className="placeholder-card__icon"><GearSixIcon size={24} aria-hidden="true" /></div>
          <div>
            <h2 id="settings-placeholder-title">设置表单将在阶段 5 接入</h2>
            <p>保存、校验与 Agent reload 规则不会复制到 React。</p>
          </div>
        </section>
        <aside className="privacy-note">
          <ShieldCheckIcon size={20} aria-hidden="true" />
          <div><strong>本地优先</strong><p>不启用远程脚本、外部字体或通用文件访问。</p></div>
        </aside>
      </div>
    </div>
  );
}
