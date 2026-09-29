import { rangeFromQuery, rangeToQuery, resolveRange } from './range';

describe('range', () => {
  const now = new Date('2026-09-29T12:00:00Z');

  it('resolves a preset relative to now', () => {
    expect(resolveRange({ kind: 'preset', preset: '1h' }, now)).toEqual({
      from: '2026-09-29T11:00:00.000Z',
      to: '2026-09-29T12:00:00.000Z',
    });
  });

  it('passes explicit bounds through as UTC instants', () => {
    const from = new Date('2026-09-29T10:00:00+02:00');
    const to = new Date('2026-09-29T10:30:00+02:00');

    expect(resolveRange({ kind: 'explicit', from, to }, now)).toEqual({
      from: '2026-09-29T08:00:00.000Z',
      to: '2026-09-29T08:30:00.000Z',
    });
  });

  it('reads explicit bounds before a preset', () => {
    const selection = rangeFromQuery({
      range: '6h',
      from: '2026-09-29T08:00:00Z',
      to: '2026-09-29T09:00:00Z',
    });

    expect(selection.kind).toBe('explicit');
  });

  it('falls back to the default preset for unknown or partial input', () => {
    expect(rangeFromQuery({ range: '3d' })).toEqual({ kind: 'preset', preset: '15m' });
    expect(rangeFromQuery({ from: '2026-09-29T08:00:00Z' })).toEqual({
      kind: 'preset',
      preset: '15m',
    });
    expect(rangeFromQuery({ from: 'yesterday', to: 'today' })).toEqual({
      kind: 'preset',
      preset: '15m',
    });
  });

  it('round-trips through the query parameters', () => {
    const explicit = {
      kind: 'explicit',
      from: new Date('2026-09-29T08:00:00Z'),
      to: new Date('2026-09-29T09:00:00Z'),
    } as const;

    expect(rangeFromQuery(rangeToQuery(explicit))).toEqual(explicit);
    expect(rangeToQuery({ kind: 'preset', preset: '24h' })).toEqual({
      range: '24h',
      from: null,
      to: null,
    });
  });
});
