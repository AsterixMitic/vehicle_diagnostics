import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { Router } from '@angular/router';
import type { EChartsCoreOption } from 'echarts/core';

import { ApiUrls } from '../../core/api/api-urls';
import {
  EvaluationMode,
  EvaluationSeries,
  EvaluationSummary,
  ModeSeriesPoint,
  ModeSummary,
} from '../../core/api/models';
import {
  formatBucket,
  formatBytes,
  formatCount,
  formatDateTime,
  formatLatency,
  formatRate,
} from '../../core/time/format';
import { rangeFromQuery, rangeToQuery, RangeSelection, resolveRange } from '../../core/time/range';
import { Chart } from '../../shared/charts/chart';
import {
  baseOptions,
  CHART_CHROME,
  ColorScheme,
  lineSeries,
  modeColor,
  Scheme,
  timeAxis,
  valueAxis,
} from '../../shared/charts/chart-theme';
import { DataState } from '../../shared/data-state/data-state';
import { ModeBadge } from '../../shared/mode-badge';
import { RangePicker } from '../../shared/range-picker/range-picker';

type LatencyKind = 'endToEnd' | 'transport' | 'backend';

const LATENCY_KINDS: readonly { readonly value: LatencyKind; readonly label: string }[] = [
  { value: 'endToEnd', label: 'Крај–крај' },
  { value: 'transport', label: 'Пренос' },
  { value: 'backend', label: 'Backend' },
];

interface TimeMetric {
  readonly title: string;
  readonly unit: string;
  readonly value: (p: ModeSeriesPoint) => number | null;
  readonly format: (v: number | null) => string;
}

const TIME_METRICS: readonly TimeMetric[] = [
  {
    title: 'Порука у секунди',
    unit: 'msg/s',
    value: (p) => p.messagesPerSecond,
    format: formatRate,
  },
  { title: 'Бајтова у секунди', unit: 'B/s', value: (p) => p.bytesPerSecond, format: formatRate },
  {
    title: 'Кашњење крај–крај, p95',
    unit: 'ms',
    value: (p) => p.endToEndP95,
    format: formatLatency,
  },
];

/**
 * Processing modes side by side for a range: the per-mode table, latency percentiles, rates and
 * p95 latency over time, rejections, and the backend's CSV export of the same range.
 */
@Component({
  selector: 'vd-evaluation',
  imports: [
    DataState,
    RangePicker,
    Chart,
    ModeBadge,
    MatCardModule,
    MatButtonModule,
    MatButtonToggleModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './evaluation.html',
  styleUrl: './evaluation.scss',
})
export class Evaluation {
  // Query parameters, bound by the router (design D4).
  readonly range = input<string | null>(null);
  readonly from = input<string | null>(null);
  readonly to = input<string | null>(null);

  private readonly router = inject(Router);
  private readonly scheme = inject(ColorScheme).scheme;
  private readonly refreshes = signal(0);

  protected readonly latencyKinds = LATENCY_KINDS;
  protected readonly latencyKind = signal<LatencyKind>('endToEnd');

  protected readonly selection = computed(() =>
    rangeFromQuery({ range: this.range(), from: this.from(), to: this.to() }),
  );

  /** Resolved once per selection or refresh: the table, the charts and the export share instants. */
  private readonly instants = computed(() => {
    this.refreshes();
    return resolveRange(this.selection(), new Date());
  });

  protected readonly summary = httpResource<EvaluationSummary>(() =>
    ApiUrls.evaluationSummary(this.instants()),
  );
  protected readonly series = httpResource<EvaluationSeries>(() =>
    ApiUrls.evaluationSeries(this.instants()),
  );
  protected readonly exportUrl = computed(() => ApiUrls.evaluationExport(this.instants()));

  // A resource in error throws on `value()`; the error itself is shown by DataState.
  protected readonly modes = computed(() =>
    this.summary.hasValue() ? this.summary.value().modes : [],
  );
  protected readonly rejections = computed(() =>
    this.summary.hasValue() ? this.summary.value().rejections : [],
  );
  private readonly points = computed(() =>
    this.series.hasValue() ? this.series.value().points : [],
  );

  protected readonly rangeText = computed(() =>
    this.summary.hasValue()
      ? `${formatDateTime(this.summary.value().from)} – ${formatDateTime(this.summary.value().to)}`
      : '',
  );

  protected readonly bucketText = computed(() =>
    this.series.hasValue() ? formatBucket(this.series.value().bucketSeconds) : '',
  );

  protected readonly latencyChart = computed(() =>
    latencyBars(this.modes(), this.latencyKind(), this.scheme()),
  );

  protected readonly timeCharts = computed(() =>
    TIME_METRICS.map((metric) => ({
      title: metric.title,
      options: timeLines(this.points(), metric, this.scheme()),
    })),
  );

  protected readonly format = {
    count: formatCount,
    rate: formatRate,
    bytes: formatBytes,
    latency: formatLatency,
  };

  protected select(selection: RangeSelection): void {
    void this.router.navigate([], {
      queryParams: rangeToQuery(selection),
      queryParamsHandling: 'merge',
    });
  }

  protected refresh(): void {
    this.refreshes.update((n) => n + 1);
  }
}

/** Percentiles on the x axis, one bar series per mode in its fixed color (color follows the entity). */
function latencyBars(
  modes: readonly ModeSummary[],
  kind: LatencyKind,
  scheme: Scheme,
): EChartsCoreOption {
  const base = baseOptions(scheme);
  const withLatency = modes.filter((m) => m[kind] !== null);
  return {
    ...base,
    tooltip: {
      ...base.tooltip,
      trigger: 'item',
      valueFormatter: (v: unknown) => `${formatLatency(v as number)} ms`,
    },
    xAxis: {
      type: 'category',
      data: ['p50', 'p95', 'p99'],
      axisTick: { show: false },
      axisLine: { lineStyle: { color: CHART_CHROME[scheme].axis } },
      axisLabel: { color: CHART_CHROME[scheme].muted },
    },
    yAxis: valueAxis(scheme, 'ms'),
    series: withLatency.map((m) => {
      const latency = m[kind];
      return {
        name: m.mode,
        type: 'bar',
        data: latency ? [latency.p50, latency.p95, latency.p99] : [],
        barMaxWidth: 24,
        barGap: '10%',
        itemStyle: { color: modeColor(m.mode, scheme), borderRadius: [4, 4, 0, 0] },
      };
    }),
  };
}

/** One line per mode over time; one metric per chart, never two scales on one axis. */
function timeLines(
  points: readonly ModeSeriesPoint[],
  metric: TimeMetric,
  scheme: Scheme,
): EChartsCoreOption {
  const base = baseOptions(scheme);
  const byMode = new Map<EvaluationMode, [number, number | null][]>();
  for (const p of points) {
    const line = byMode.get(p.mode) ?? [];
    line.push([Date.parse(p.bucketStart), metric.value(p)]);
    byMode.set(p.mode, line);
  }
  return {
    ...base,
    tooltip: {
      ...base.tooltip,
      trigger: 'axis',
      valueFormatter: (v: unknown) => `${metric.format(v as number | null)} ${metric.unit}`,
    },
    xAxis: timeAxis(scheme),
    yAxis: valueAxis(scheme, metric.unit),
    series: [...byMode.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([mode, data]) => lineSeries(mode, modeColor(mode, scheme), data)),
  };
}
