import {
  ABSENT,
  formatBucket,
  formatBytes,
  formatCount,
  formatDateTime,
  formatLatency,
  formatRate,
  formatTime,
  formatValue,
} from './format';

describe('format', () => {
  it('uses the Serbian decimal comma and fixed decimals per metric', () => {
    expect(formatLatency(6.006)).toBe('6,01');
    expect(formatRate(1)).toBe('1,000');
    expect(formatBytes(402.78)).toBe('403');
  });

  it('groups thousands with a dot', () => {
    expect(formatCount(1234567)).toBe('1.234.567');
  });

  it('shows an absent value as a dash, never as zero', () => {
    expect(formatLatency(null)).toBe(ABSENT);
    expect(formatBytes(undefined)).toBe(ABSENT);
    expect(formatValue(Number.NaN)).toBe(ABSENT);
    expect(formatTime(null)).toBe(ABSENT);
  });

  it('keeps physical values short', () => {
    expect(formatValue(1726.25)).toBe('1.726,25');
    expect(formatValue(60)).toBe('60');
  });

  it('formats times in the given zone', () => {
    expect(formatTime('2026-09-29T13:35:09Z', 'UTC')).toBe('13:35:09');
    expect(formatDateTime('2026-09-29T13:35:09Z', 'UTC')).toContain('13:35:09');
  });

  it('states bucket widths in the largest whole unit', () => {
    expect(formatBucket(30)).toBe('30 s');
    expect(formatBucket(300)).toBe('5 мин');
    expect(formatBucket(3600)).toBe('1 ч');
  });
});
