import { DestroyRef, inject, Injectable, signal } from '@angular/core';

import { EvaluationMode } from '../../core/api/models';

/**
 * Chart colors (design D6). Mode hues are fixed per mode, never cycled, so P2 is the same color
 * on every chart; the four were validated for colorblind separation and contrast in both themes
 * with the dataviz palette validator. `unknown` is neutral gray: it is not a mode.
 */
export const MODE_COLORS: Readonly<
  Record<'light' | 'dark', Readonly<Record<EvaluationMode, string>>>
> = {
  light: { P0: '#2a78d6', P1: '#eb6834', P2: '#1baf7a', P3: '#eda100', unknown: '#898781' },
  dark: { P0: '#3987e5', P1: '#d95926', P2: '#199e70', P3: '#c98500', unknown: '#898781' },
};

/** Recessive chrome: text in ink tokens, hairline grid, never the data color. */
export const CHART_CHROME = {
  light: {
    primary: '#0b0b0b',
    secondary: '#52514e',
    muted: '#898781',
    grid: '#e1e0d9',
    axis: '#c3c2b7',
    surface: '#fcfcfb',
  },
  dark: {
    primary: '#ffffff',
    secondary: '#c3c2b7',
    muted: '#898781',
    grid: '#2c2c2a',
    axis: '#383835',
    surface: '#1a1a19',
  },
} as const;

export type Scheme = 'light' | 'dark';

/** The system color scheme as a signal; charts are canvas, so they need the values, not CSS. */
@Injectable({ providedIn: 'root' })
export class ColorScheme {
  readonly scheme = signal<Scheme>('light');

  constructor() {
    const query =
      typeof window !== 'undefined' && window.matchMedia
        ? window.matchMedia('(prefers-color-scheme: dark)')
        : null;
    if (!query) {
      return;
    }
    this.scheme.set(query.matches ? 'dark' : 'light');
    const listener = (event: MediaQueryListEvent) =>
      this.scheme.set(event.matches ? 'dark' : 'light');
    query.addEventListener('change', listener);
    inject(DestroyRef).onDestroy(() => query.removeEventListener('change', listener));
  }
}

export function modeColor(mode: EvaluationMode, scheme: Scheme): string {
  return MODE_COLORS[scheme][mode];
}

/** Options shared by every chart: transparent background, ink-token text, hairline grid. */
export function baseOptions(scheme: Scheme) {
  const chrome = CHART_CHROME[scheme];
  return {
    backgroundColor: 'transparent',
    animationDuration: 300,
    textStyle: { color: chrome.secondary, fontFamily: 'inherit' },
    grid: { left: 8, right: 16, top: 40, bottom: 8, containLabel: true },
    legend: {
      top: 0,
      left: 0,
      icon: 'roundRect',
      itemWidth: 14,
      itemHeight: 3,
      textStyle: { color: chrome.secondary },
    },
    tooltip: {
      backgroundColor: scheme === 'dark' ? '#262624' : '#ffffff',
      borderColor: scheme === 'dark' ? 'rgba(255,255,255,0.10)' : 'rgba(11,11,11,0.10)',
      textStyle: { color: chrome.primary },
    },
  };
}

export function timeAxis(scheme: Scheme) {
  const chrome = CHART_CHROME[scheme];
  return {
    type: 'time' as const,
    axisLine: { lineStyle: { color: chrome.axis } },
    axisTick: { show: false },
    axisLabel: { color: chrome.muted, hideOverlap: true },
    splitLine: { show: false },
  };
}

export function valueAxis(scheme: Scheme, name?: string) {
  const chrome = CHART_CHROME[scheme];
  return {
    type: 'value' as const,
    name,
    nameTextStyle: { color: chrome.muted, align: 'left' as const },
    axisLine: { show: false },
    axisLabel: { color: chrome.muted },
    splitLine: { lineStyle: { color: chrome.grid, width: 1, type: 'solid' as const } },
  };
}

/** 2px line, round caps, no per-point symbols (the crosshair finds the X). */
export function lineSeries(
  name: string,
  color: string,
  data: readonly (readonly [number, number | null])[],
) {
  return {
    name,
    type: 'line' as const,
    data,
    showSymbol: false,
    symbolSize: 8,
    connectNulls: false,
    lineStyle: { width: 2, color, cap: 'round' as const, join: 'round' as const },
    itemStyle: { color },
    emphasis: { focus: 'series' as const },
  };
}
