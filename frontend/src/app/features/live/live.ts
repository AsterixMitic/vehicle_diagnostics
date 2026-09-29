import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { Router } from '@angular/router';
import type { EChartsCoreOption } from 'echarts/core';

import { ApiUrls } from '../../core/api/api-urls';
import { Device, LatestSignal } from '../../core/api/models';
import { signalName, unitLabel } from '../../core/api/signals';
import { LiveConnection, LiveSeries, LiveStream } from '../../core/live/live-stream';
import { formatTime, formatValue } from '../../core/time/format';
import { resolveRange } from '../../core/time/range';
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

interface ValueCard {
  readonly id: string;
  readonly name: string;
  readonly value: string;
  readonly unit: string;
  readonly sampledAt: string;
}

const CONNECTION_TEXT: Readonly<Record<LiveConnection, string>> = {
  idle: 'није повезано',
  connecting: 'повезивање…',
  open: 'уживо',
  reconnecting: 'поновно повезивање…',
};

/**
 * Latest value of every signal and a live chart per signal for one device. Values are loaded
 * first, so the cards are never blank, then updated from the stream (design D5).
 */
@Component({
  selector: 'vd-live',
  imports: [DataState, Chart, MatCardModule, MatFormFieldModule, MatSelectModule],
  providers: [LiveStream],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './live.html',
  styleUrl: './live.scss',
})
export class Live {
  /** Query parameter `device`. */
  readonly device = input<string | null>(null);

  private readonly router = inject(Router);
  private readonly scheme = inject(ColorScheme).scheme;
  protected readonly stream = inject(LiveStream);

  protected readonly devices = httpResource<Device[]>(() =>
    ApiUrls.devices(resolveRange({ kind: 'preset', preset: '15m' }, new Date())),
  );

  protected readonly latest = httpResource<LatestSignal[]>(() => {
    const device = this.device();
    return device ? ApiUrls.latestSignals(device) : undefined;
  });

  protected readonly connectionText = computed(() => CONNECTION_TEXT[this.stream.connection()]);

  protected readonly cards = computed<readonly ValueCard[]>(() => {
    const cards = new Map<string, ValueCard>();
    for (const s of this.latest.hasValue() ? this.latest.value() : []) {
      cards.set(s.name, card(s.name, s.value, s.unit, s.sampledAt));
    }
    const reading = this.stream.lastReading();
    if (reading && reading.deviceId === this.device()) {
      for (const s of reading.readings) {
        cards.set(s.name, card(s.name, s.value, s.unit, reading.timestampDevice));
      }
    }
    return [...cards.values()].sort((a, b) => a.id.localeCompare(b.id));
  });

  protected readonly charts = computed(() =>
    this.stream.series().map((series) => ({
      id: series.name,
      title: signalName(series.name),
      options: this.chartOptions(series),
    })),
  );

  constructor() {
    // Stream the selected device; a new selection replaces the previous stream.
    effect(() => {
      const device = this.device();
      if (device) {
        this.stream.connect(device);
      } else {
        this.stream.close();
      }
    });

    // Without a selection, pick the first device seen, keeping the choice in the URL.
    effect(() => {
      const first = this.devices.hasValue() ? this.devices.value()[0] : undefined;
      if (!this.device() && first) {
        this.choose(first.deviceId, true);
      }
    });
  }

  protected choose(deviceId: string, replaceUrl = false): void {
    void this.router.navigate([], {
      queryParams: { device: deviceId },
      queryParamsHandling: 'merge',
      replaceUrl,
    });
  }

  private chartOptions(series: LiveSeries): EChartsCoreOption {
    const scheme = this.scheme();
    return {
      ...baseOptions(scheme),
      grid: { left: 8, right: 16, top: 16, bottom: 8, containLabel: true },
      legend: { show: false },
      tooltip: {
        ...baseOptions(scheme).tooltip,
        trigger: 'axis',
        valueFormatter: (v: unknown) => `${formatValue(v as number)} ${unitLabel(series.unit)}`,
      },
      xAxis: timeAxis(scheme),
      yAxis: { ...valueAxis(scheme), scale: true },
      // The line wears the device's mode color, as that mode does on every other chart.
      series: [
        lineSeries(
          signalName(series.name),
          modeColor(this.stream.lastReading()?.processingMode ?? 'P0', scheme),
          series.points.map((p) => [p.time, p.value] as const),
        ),
      ],
    };
  }
}

function card(id: string, value: number, unit: string, sampledAt: string): ValueCard {
  return {
    id,
    name: signalName(id),
    value: formatValue(value),
    unit: unitLabel(unit),
    sampledAt: formatTime(sampledAt),
  };
}
