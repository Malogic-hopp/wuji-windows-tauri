/** 展示层格式化（React 不做时长聚合，只做 09 §8.4 DTO 的显示换算）。 */

/**
 * Int64String 安全解析（审核 R07）：
 * - 时长一律走 BigInt，避免 number 精度截断；
 * - 时间戳转 Date 前校验 |ms| <= 2^53 - 1，超出安全范围返回 null。
 */
function parseInt64(text: string): bigint | null {
  if (!/^-?\d+$/.test(text)) return null;
  try {
    return BigInt(text);
  } catch {
    return null;
  }
}

const MAX_SAFE_MS = BigInt(Number.MAX_SAFE_INTEGER);
const MIN_SAFE_MS = -MAX_SAFE_MS;

function toSafeMs(value: bigint): number | null {
  if (value > MAX_SAFE_MS || value < MIN_SAFE_MS) return null;
  return Number(value);
}

function safeTimestampMs(text: string): number | null {
  const value = parseInt64(text);
  return value === null ? null : toSafeMs(value);
}

/** 毫秒 → "X 小时 Y 分钟" / "Y 分钟" / "X 秒"。 */
export function formatDuration(msText: string): string {
  const ms = parseInt64(msText);
  if (ms === null || ms < 0n) return '—';
  const seconds = (ms + 500n) / 1000n;
  if (seconds < 60n) return `${seconds.toString()} 秒`;
  const minutes = seconds / 60n;
  if (minutes < 60n) return `${minutes.toString()} 分钟`;
  const hours = minutes / 60n;
  const rest = minutes % 60n;
  if (hours < 24n) {
    return rest === 0n ? `${hours.toString()} 小时` : `${hours.toString()} 小时 ${rest.toString()} 分钟`;
  }
  const days = hours / 24n;
  const restHours = hours % 24n;
  return restHours === 0n ? `${days.toString()} 天` : `${days.toString()} 天 ${restHours.toString()} 小时`;
}

/** UTC 毫秒 → 指定时区的 HH:mm。 */
export function formatClock(msText: string, timeZoneId: string): string {
  const ms = safeTimestampMs(msText);
  if (ms === null) return '—';
  return new Intl.DateTimeFormat('zh-CN', {
    timeZone: timeZoneId,
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(new Date(ms));
}

/** UTC 毫秒 → 指定时区的完整日期时间。 */
export function formatDateTime(msText: string, timeZoneId: string): string {
  const ms = safeTimestampMs(msText);
  if (ms === null) return '—';
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
  const ms = safeTimestampMs(msText);
  if (ms === null) return '—';
  const age = Math.max(0, Math.round((nowMs - ms) / 1000));
  if (age < 60) return `${String(age)} 秒前`;
  const minutes = Math.floor(age / 60);
  if (minutes < 60) return `${String(minutes)} 分钟前`;
  return `${String(Math.floor(minutes / 60))} 小时前`;
}
