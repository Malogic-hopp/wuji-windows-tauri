import { ArrowRightIcon, ChartLineUpIcon } from '@phosphor-icons/react';

export default function DashboardPage() {
  return (
    <div className="page page-enter">
      <header className="page-header">
        <div>
          <p className="eyebrow">今日概览</p>
          <h1 tabIndex={-1}>把时间，看清楚一点</h1>
          <p>当前阶段已接通本地 Bridge 与 Agent。真实活动摘要将在阶段 4 迁入。</p>
        </div>
      </header>
      <section className="placeholder-card" aria-labelledby="dashboard-placeholder-title">
        <div className="placeholder-card__icon"><ChartLineUpIcon size={24} aria-hidden="true" /></div>
        <div>
          <h2 id="dashboard-placeholder-title">Dashboard 数据切片尚未启用</h2>
          <p>这里将复用现有 Application 层的概览用例，不会从浏览器直接读取 SQLite。</p>
        </div>
        <span className="phase-label">阶段 4 <ArrowRightIcon size={16} aria-hidden="true" /></span>
      </section>
    </div>
  );
}
