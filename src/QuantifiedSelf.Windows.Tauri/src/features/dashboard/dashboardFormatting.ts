type Locale = string | readonly string[] | undefined;

export function formatDuration(value: number, locale?: Locale): string {
  const totalSeconds = Math.max(0, Math.round(value));
  const hours = Math.floor(totalSeconds / 3_600);
  const minutes = Math.floor((totalSeconds % 3_600) / 60);
  const seconds = totalSeconds % 60;
  const parts: string[] = [];

  if (hours > 0) parts.push(formatUnit(hours, 'hour', locale));
  if (minutes > 0) parts.push(formatUnit(minutes, 'minute', locale));
  if (hours === 0 && (seconds > 0 || parts.length === 0)) {
    parts.push(formatUnit(seconds, 'second', locale));
  }

  return new Intl.ListFormat(locale, { style: 'short', type: 'unit' }).format(parts);
}

export function formatCount(value: number, locale?: Locale): string {
  return new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(Math.max(0, value));
}

export function formatLastUpdated(timestamp: number, locale?: Locale): string {
  if (timestamp <= 0) return '尚未更新';
  return new Intl.DateTimeFormat(locale, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).format(timestamp);
}

export function formatSessionRange(
  startedAtUtc: string,
  endedAtUtc: string | undefined,
  locale?: Locale,
): string {
  const start = parseDate(startedAtUtc);
  if (!start) return '时间未知';

  const startText = new Intl.DateTimeFormat(locale, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(start);
  const end = endedAtUtc ? parseDate(endedAtUtc) : undefined;
  if (!end) return `${startText} 起`;

  const endOptions: Intl.DateTimeFormatOptions = sameLocalDate(start, end)
    ? { hour: '2-digit', minute: '2-digit' }
    : { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' };
  return `${startText} – ${new Intl.DateTimeFormat(locale, endOptions).format(end)}`;
}

function formatUnit(value: number, unit: Intl.NumberFormatOptions['unit'], locale?: Locale) {
  return new Intl.NumberFormat(locale, {
    style: 'unit',
    unit,
    unitDisplay: 'short',
  }).format(value);
}

function parseDate(value: string): Date | undefined {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? undefined : date;
}

function sameLocalDate(left: Date, right: Date) {
  return left.getFullYear() === right.getFullYear()
    && left.getMonth() === right.getMonth()
    && left.getDate() === right.getDate();
}
