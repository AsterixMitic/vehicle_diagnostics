/** A resolved range as the API takes it: ISO 8601 UTC instants, `from` inclusive, `to` exclusive. */
export interface InstantRange {
  readonly from: string;
  readonly to: string;
}

export type RangePreset = '15m' | '1h' | '6h' | '24h';

export const RANGE_PRESETS: readonly {
  readonly value: RangePreset;
  readonly label: string;
  readonly minutes: number;
}[] = [
  { value: '15m', label: '15 мин', minutes: 15 },
  { value: '1h', label: '1 сат', minutes: 60 },
  { value: '6h', label: '6 сати', minutes: 360 },
  { value: '24h', label: '24 сата', minutes: 1440 },
];

export const DEFAULT_PRESET: RangePreset = '15m';

/** What the user chose: a preset relative to now, or explicit bounds. */
export type RangeSelection =
  | { readonly kind: 'preset'; readonly preset: RangePreset }
  | { readonly kind: 'explicit'; readonly from: Date; readonly to: Date };

/** The query parameters a selection is stored in (design D4); absent keys are `null`. */
export interface RangeQuery {
  readonly range: string | null;
  readonly from: string | null;
  readonly to: string | null;
}

/**
 * Presets are resolved when the request is built, so "last hour" moves with time. Explicit
 * bounds are passed through even if reversed: the backend explains what is wrong with them.
 */
export function resolveRange(selection: RangeSelection, now: Date): InstantRange {
  if (selection.kind === 'explicit') {
    return { from: selection.from.toISOString(), to: selection.to.toISOString() };
  }
  const minutes = RANGE_PRESETS.find((p) => p.value === selection.preset)?.minutes ?? 15;
  return { from: new Date(now.getTime() - minutes * 60_000).toISOString(), to: now.toISOString() };
}

/** Explicit bounds win when both parse; otherwise a known preset; otherwise the default preset. */
export function rangeFromQuery(query: Partial<RangeQuery>): RangeSelection {
  const from = parseInstant(query.from);
  const to = parseInstant(query.to);
  if (from && to) {
    return { kind: 'explicit', from, to };
  }
  const preset = RANGE_PRESETS.find((p) => p.value === query.range)?.value ?? DEFAULT_PRESET;
  return { kind: 'preset', preset };
}

export function rangeToQuery(selection: RangeSelection): RangeQuery {
  return selection.kind === 'preset'
    ? { range: selection.preset, from: null, to: null }
    : { range: null, from: selection.from.toISOString(), to: selection.to.toISOString() };
}

function parseInstant(value: string | null | undefined): Date | null {
  if (!value) {
    return null;
  }
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}
