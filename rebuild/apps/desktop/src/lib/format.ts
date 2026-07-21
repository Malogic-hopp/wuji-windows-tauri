/** 展示层格式化（React 不做时长聚合，只做 09 §8.4 DTO 的显示换算）。 */

/** 毫秒 → "X 小时 Y 分钟" / "Y 分钟" / "X 秒"。 */
export function formatDuration(msText: string): string {
  const ms = Number(msText);
  if (!Number.isFinite(ms) || ms < 0) return '—';
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) return `${String(seconds)} 秒`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${String(minutes)} 分钟`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours < 24) {
    return rest === 0 ? `${String(hours)} 小时` : `${String(hours)} 小时 ${String(rest)} 分钟`;
  }
  const days = Math.floor(hours / 24);
  const restHours = hours % 24;
  return restHours === 0 ? `${String(days)} 天` : `${String(days)} 天 ${String(restHours)} 小时`;
}

/** UTC 毫秒 → 指定时区的 HH:mm。 */
export function formatClock(msText: string, timeZoneId: string): string {
  const ms = Number(msText);
  if (!Number.isFinite(ms)) return '—';
  return new Intl.DateTimeFormat('zh-CN', {
    timeZone: timeZoneId,
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(new Date(ms));
}

/** UTC 毫秒 → 指定时区的完整日期时间。 */
export function formatDateTime(msText: string, timeZoneId: string): string {
  const ms = Number(msText);
  if (!Number.isFinite(ms)) return '—';
  return new Intl.DateTimeFormat('zh-CN', {
    timeZone: timeZoneId,
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  }).format(new Date(ms));
}

/** 时间戳相对年龄（诊断页心跳用）。 */
export function formatAge(msText: string, nowMs: number): string {
  const ms = Number(msText);
  if (!Number.isFinite(ms)) return '—';
  const age = Math.max(0, Math.round((nowMs - ms) / 1000));
  if (age < 60) return `${String(age)} 秒前`;
  const minutes = Math.floor(age / 60);
  if (minutes < 60) return `${String(minutes)} 分钟前`;
  return `${String(Math.floor(minutes / 60))} 小时前`;
}
