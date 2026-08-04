import type {
  AppPaletteEntryDto,
  CompositionBucketDto,
  Int64String,
} from '../../../types/wuji-core';
import { formatDeltaMs, slotToToken } from '../statsModel';

function paletteSlot(palette: readonly AppPaletteEntryDto[], appId: Int64String): number {
  return palette.find((entry) => entry.app.appId === appId)?.slot ?? -1;
}

/** 日桶：横向堆叠条；周桶：纵向堆叠柱。isCurrent 桶弱化（进行中）。 */
export function AppComposition({
  buckets,
  palette,
  cutoffLocalTime = '',
}: {
  buckets: readonly CompositionBucketDto[];
  palette: readonly AppPaletteEntryDto[];
  /** 当前桶 hover/焦点"截至 HH:MM"（阶段四 4.3；阶段五由 live 轮询提供）。 */
  cutoffLocalTime?: string;
}) {
  if (buckets.length === 0) {
    return (
      <figure className="chart" aria-label="应用构成">
        <div className="state-block__title state-block__title--small">暂无应用构成数据</div>
      </figure>
    );
  }
  const isWeek = buckets[0]?.bucketKind === 'week';
  return (
    <figure className="chart" aria-label="应用构成">
      <div
        className={
          isWeek ? 'chart__body chart__body--comp-week' : 'chart__body chart__body--comp-day'
        }
      >
        {buckets.map((bucket) => {
          const bucketTotal =
            bucket.apps.reduce((sum, a) => sum + Number(a.activeDurationMs), 0) +
            Number(bucket.othersActiveMs);
          const currentSuffix =
            bucket.isCurrent && cutoffLocalTime !== '' ? `（截至 ${cutoffLocalTime}）` : '';
          const statusLabel = bucket.isCurrent
            ? `，进行中${currentSuffix}`
            : !bucket.hasData
              ? '，当日无记录数据'
              : '';
          const bucketLabel = `${isWeek ? `${bucket.startDate} 至 ${bucket.endDate}` : bucket.startDate}${statusLabel}`;
          const bucketAria = `${bucketLabel}，总时长 ${formatDeltaMs(String(bucketTotal) as Int64String)}`;
          return (
            <div
              key={`${bucket.startDate}-${bucket.endDate}`}
              className={isWeek ? 'comp-col' : 'comp-row'}
              data-current={bucket.isCurrent || undefined}
              tabIndex={0}
              role="img"
              aria-label={bucketAria}
              title={
                bucket.isCurrent
                  ? `进行中${currentSuffix}`
                  : !bucket.hasData
                    ? '当日无记录数据'
                    : undefined
              }
            >
              <div className={isWeek ? 'comp-col__stack' : 'comp-row__stack'}>
                {!bucket.hasData && (
                  <div
                    className="comp-seg comp-seg--nodata"
                    style={{ flexGrow: 1 }}
                    role="img"
                    aria-label="当日无记录数据"
                    title="当日无记录数据"
                  />
                )}
                {bucket.apps.map((entry) => {
                  return (
                    <div
                      key={entry.app.appId}
                      className="comp-seg"
                      style={{
                        // flex-grow 按原始毫秒权重分配，避免逐段四舍五入的累计误差（阶段四 review 代码层 3）。
                        flexGrow: Number(entry.activeDurationMs),
                        backgroundColor: slotToToken(paletteSlot(palette, entry.app.appId)),
                      }}
                      role="img"
                      aria-label={`${entry.app.displayName} ${formatDeltaMs(entry.activeDurationMs)}`}
                      title={`${entry.app.displayName} ${formatDeltaMs(entry.activeDurationMs)}`}
                    />
                  );
                })}
                {Number(bucket.othersActiveMs) > 0 && (
                  <div
                    className="comp-seg comp-seg--other"
                    style={{ flexGrow: Number(bucket.othersActiveMs) }}
                    role="img"
                    aria-label={`其他 ${formatDeltaMs(bucket.othersActiveMs)}`}
                    title={`其他 ${formatDeltaMs(bucket.othersActiveMs)}`}
                  />
                )}
              </div>
              <div className="comp-label">
                {/* 周桶只显示日期范围，不加总时长。 */}
                {isWeek
                  ? `${bucket.startDate.slice(5)}–${bucket.endDate.slice(5)}`
                  : bucket.endDate.slice(5)}
              </div>
              {/* 日桶：日期旁弱化的当日总时长；无记录日显示 "—"（区别于有记录零活跃的 0m）。 */}
              {!isWeek && (
                <div className="comp-row__total">
                  {bucket.hasData ? formatDeltaMs(String(bucketTotal) as Int64String) : '—'}
                </div>
              )}
            </div>
          );
        })}
      </div>
      <figcaption className="chart__legend">
        {palette.map((entry) => (
          <span
            key={entry.app.appId}
            className="legend-chip"
            style={{ '--chip-color': slotToToken(entry.slot) } as React.CSSProperties}
          >
            {entry.app.displayName}
          </span>
        ))}
        <span className="legend-chip legend-chip--other">其他</span>
      </figcaption>
    </figure>
  );
}
