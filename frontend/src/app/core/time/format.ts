/** `sr-RS` formatting (design D7): decimal comma, Cyrillic, `—` for an absent value, never `0`. */

export const ABSENT = '—';

const LOCALE = 'sr-RS';

const numberFormats = new Map<number, Intl.NumberFormat>();

export function formatNumber(value: number | null | undefined, decimals: number): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return ABSENT;
  }
  let format = numberFormats.get(decimals);
  if (!format) {
    format = new Intl.NumberFormat(LOCALE, {
      minimumFractionDigits: decimals,
      maximumFractionDigits: decimals,
    });
    numberFormats.set(decimals, format);
  }
  return format.format(value);
}

/** Milliseconds, two decimals. */
export const formatLatency = (ms: number | null | undefined): string => formatNumber(ms, 2);

/** Messages or bytes per second, three decimals. */
export const formatRate = (value: number | null | undefined): string => formatNumber(value, 3);

/** Payload sizes in bytes, whole. */
export const formatBytes = (value: number | null | undefined): string => formatNumber(value, 0);

export const formatCount = (value: number | null | undefined): string => formatNumber(value, 0);

/** A physical value: up to two decimals, without trailing zeros. */
export function formatValue(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return ABSENT;
  }
  return new Intl.NumberFormat(LOCALE, { maximumFractionDigits: 2 }).format(value);
}

export function formatDateTime(iso: string | null | undefined, timeZone?: string): string {
  const date = toDate(iso);
  return date
    ? new Intl.DateTimeFormat(LOCALE, {
        dateStyle: 'medium',
        timeStyle: 'medium',
        timeZone,
      }).format(date)
    : ABSENT;
}

export function formatTime(iso: string | null | undefined, timeZone?: string): string {
  const date = toDate(iso);
  return date
    ? new Intl.DateTimeFormat(LOCALE, { timeStyle: 'medium', timeZone }).format(date)
    : ABSENT;
}

/** A bucket width as the page states it, e.g. `30 s`, `5 мин`, `1 ч`. */
export function formatBucket(seconds: number): string {
  if (seconds % 3600 === 0) {
    return `${seconds / 3600} ч`;
  }
  if (seconds % 60 === 0) {
    return `${seconds / 60} мин`;
  }
  return `${seconds} s`;
}

function toDate(iso: string | null | undefined): Date | null {
  if (!iso) {
    return null;
  }
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? null : date;
}
