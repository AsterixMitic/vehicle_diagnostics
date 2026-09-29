import { httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { Router } from '@angular/router';
import type { EChartsCoreOption } from 'echarts/core';

import { ApiUrls } from '../../core/api/api-urls';
import { Device, LatestSignal, SignalPoint, SignalSeries } from '../../core/api/models';
import { signalName, unitLabel } from '../../core/api/signals';
import { formatBucket, formatDateTime, formatValue } from '../../core/time/format';
import { rangeFromQuery, rangeToQuery, RangeSelection, resolveRange } from '../../core/time/range';
import { Chart } from '../../shared/charts/chart';
import {
  baseOptions,
  ColorScheme,
  lineSeries,
  modeColor,
  timeAxis,
  valueAxis,
} from '../../shared/charts/chart-theme';
import { DataState } from '../../shared/data-state/data-state';
import { RangePicker } from '../../shared/range-picker/range-picker';

/** One signal of one device over a range: bucketed average with its minimum–maximum band. */
@Component({
  selector: 'vd-history',
  imports: [
    DataState,
    RangePicker,
    Chart,
    MatCardModule,
    MatFormFieldModule,
    MatSelectModule,
    MatButtonModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './history.html',
  styleUrl: './history.scss',
})
export class History {
  // Query parameters, bound by the router (design D4).
  readonly device = input<string | null>(null);
  readonly signal = input<string | null>(null);
  readonly range = input<string | null>(null);
  readonly from = input<string | null>(null);
  readonly to = input<string | null>(null);

  private readonly router = inject(Router);
  private readonly scheme = inject(ColorScheme).scheme;
  private readonly refreshes = signal(0);

  protected readonly selection = computed(() =>
    rangeFromQuery({ range: this.range(), from: this.from(), to: this.to() }),
  );

  /** Resolved once per selection or refresh, so all requests of the page use the same instants. */
  private readonly instants = computed(() => {
    this.refreshes();
    return resolveRange(this.selection(), new Date());
  });

  protected readonly devices = httpResource<Device[]>(() => ApiUrls.devices(this.instants()));

  protected readonly signals = httpResource<LatestSignal[]>(() => {
    const device = this.device();
    return device ? ApiUrls.latestSignals(device) : undefined;
  });

  protected readonly series = httpResource<SignalSeries>(() => {
    const device = this.device();
    const signalId = this.signal();
    return device && signalId ? ApiUrls.signalSeries(device, signalId, this.instants()) : undefined;
  });

  // A resource in error throws on `value()`; the error itself is shown by DataState.
  protected readonly deviceList = computed(() =>
    this.devices.hasValue() ? this.devices.value() : [],
  );
  protected readonly signalList = computed(() =>
    this.signals.hasValue() ? this.signals.value() : [],
  );
  protected readonly points = computed(() =>
    this.series.hasValue() ? this.series.value().points : [],
  );

  protected readonly signalLabel = computed(() => {
    const id = this.signal();
    return id ? signalName(id) : '';
  });

  protected readonly bucketText = computed(() =>
    this.series.hasValue() ? formatBucket(this.series.value().bucketSeconds) : '',
  );

  protected readonly options = computed<EChartsCoreOption | null>(() => {
    const points = this.points();
    if (points.length === 0) {
      return null;
    }
    const signals = this.signalList();
    const unit = unitLabel(signals.find((s) => s.name === this.signal())?.unit ?? '');
    const mode = signals[0]?.processingMode ?? 'P0';
    return bandChart(points, unit, modeColor(mode, this.scheme()), this.scheme());
  });

  protected readonly signalName = signalName;

  constructor() {
    // Fill a missing device or signal with the first one available, keeping it in the URL.
    effect(() => {
      const first = this.deviceList()[0];
      if (!this.device() && first) {
        this.navigate({ device: first.deviceId, signal: null }, true);
      }
    });
    effect(() => {
      const available = this.signalList();
      if (!this.signal() && available.length > 0) {
        const preferred = available.find((s) => s.name === 'engine_rpm') ?? available[0];
        this.navigate({ signal: preferred.name }, true);
      }
    });
  }

  protected chooseDevice(deviceId: string): void {
    this.navigate({ device: deviceId, signal: null });
  }

  protected chooseSignal(signalId: string): void {
    this.navigate({ signal: signalId });
  }

  protected select(selection: RangeSelection): void {
    this.navigate({ ...rangeToQuery(selection) });
  }

  protected refresh(): void {
    this.refreshes.update((n) => n + 1);
  }

  private navigate(queryParams: Record<string, string | null>, replaceUrl = false): void {
    void this.router.navigate([], { queryParams, queryParamsHandling: 'merge', replaceUrl });
  }
}

/**
 * The band is drawn as an invisible minimum line with the (maximum − minimum) difference stacked
 * on it as a 10% wash; the average is the 2px line. The tooltip reads the original point, so it
 * shows real minimum and maximum rather than the stacked difference.
 */
function bandChart(
  points: readonly SignalPoint[],
  unit: string,
  color: string,
  scheme: 'light' | 'dark',
): EChartsCoreOption {
  const base = baseOptions(scheme);
  const time = (p: SignalPoint) => Date.parse(p.bucketStart);
  return {
    ...base,
    tooltip: {
      ...base.tooltip,
      trigger: 'axis',
      formatter: (params: { dataIndex: number }[]) => {
        const point = points[params[0]?.dataIndex ?? -1];
        if (!point) {
          return '';
        }
        return [
          formatDateTime(point.bucketStart),
          `<strong>${formatValue(point.average)}</strong> ${unit} просек`,
          `${formatValue(point.minimum)} – ${formatValue(point.maximum)} ${unit} мин–макс`,
          `${point.samples} узорака`,
        ].join('<br>');
      },
    },
    xAxis: timeAxis(scheme),
    yAxis: { ...valueAxis(scheme, unit), scale: true },
    series: [
      {
        name: 'мин',
        type: 'line',
        data: points.map((p) => [time(p), p.minimum]),
        stack: 'band',
        lineStyle: { opacity: 0 },
        showSymbol: false,
        silent: true,
        tooltip: { show: false },
      },
      {
        name: 'мин–макс',
        type: 'line',
        data: points.map((p) => [time(p), p.maximum - p.minimum]),
        stack: 'band',
        lineStyle: { opacity: 0 },
        areaStyle: { color, opacity: 0.1 },
        itemStyle: { color },
        showSymbol: false,
      },
      {
        ...lineSeries(
          'просек',
          color,
          points.map((p) => [time(p), p.average] as const),
        ),
      },
    ],
    legend: { ...base.legend, data: ['просек', 'мин–макс'] },
  };
}
