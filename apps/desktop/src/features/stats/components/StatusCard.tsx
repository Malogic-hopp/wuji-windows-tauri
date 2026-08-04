import type { LiveStatusDto, SummaryDto } from '../../../types/wuji-core';
import { formatDuration } from '../../../lib/format';
import { formatDeltaMs, mapDirectionDisplay, mapSummaryText } from '../statsModel';

/**
 * ① 今日状态（10 §4.1；阶段四外部评审布局）：内容组件，卡片外壳由 StatsPage 主卡提供。
 * 信息层级：一级 = 小标签"今日活跃" + 大号紧凑数字（formatDeltaMs）+ 同行右侧工作块；
 * 二级 = "截至 HH:MM"（dim）与较昨日/较近 7 日比较；三级 = 摘要句。
 * 完整语义保留在大号数字的 aria-label（"今日截至 HH:MM 活跃 X 小时 Y 分钟"）。
 * 实时数字/比较来自 LiveStatusDto（轮询载荷），摘要句子来自 home 快照的 SummaryDto（阶段零 P0-1）。
 */
export function StatusCard({
  live,
  summary,
}: {
  live: LiveStatusDto;
  summary: SummaryDto;
}) {
  const yesterday = mapDirectionDisplay(live.yesterdaySame, live.todayActiveMs);
  const last7 = mapDirectionDisplay(live.last7AvgSame, live.todayActiveMs);
  const summaryText = mapSummaryText(summary);
  return (
    <section className="stats-status" aria-label="今日状态">
      <div className="stats-status__head">
        <span className="stats-status__label">今日活跃</span>
        <span
          className="stats-status__main"
          aria-label={`今日截至 ${live.cutoffLocalTime} 活跃 ${formatDuration(live.todayActiveMs)}`}
        >
          {formatDeltaMs(live.todayActiveMs)}
        </span>
      </div>
      {/* 截止时刻与工作块合并为一行辅助信息：避免工作块被 margin-left:auto 推到
          分隔线旁、误读为本周区域内容（review-2 主卡）。 */}
      <div className="stats-status__meta">
        截至 {live.cutoffLocalTime} · {live.workBlockCount} 个工作块
      </div>
      <div className="stats-status__compare">
        {yesterday.text !== '' && (
          <span className="stats-status__cmp">
            较昨日同时刻{' '}
            <span className={yesterday.showArrow ? 'stats-status__dir' : undefined}>
              {yesterday.text}
            </span>
          </span>
        )}
        {last7.text !== '' ? (
          <span className="stats-status__cmp">
            较近 7 日均同时刻{' '}
            <span className={last7.showArrow ? 'stats-status__dir' : undefined}>
              {last7.text}
            </span>
            （基于 {live.last7AvgSame.sampleDays} 个有效日）
          </span>
        ) : (
          <span className="stats-status__cmp stats-status__cmp--dim">历史样本不足</span>
        )}
      </div>
      {summaryText !== '' && <div className="stats-status__summary">{summaryText}</div>}
    </section>
  );
}
