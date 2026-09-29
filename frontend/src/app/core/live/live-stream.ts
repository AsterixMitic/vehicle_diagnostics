import { computed, DestroyRef, inject, Injectable, InjectionToken, signal } from '@angular/core';

import { ApiUrls } from '../api/api-urls';
import { LiveReading } from '../api/models';

/** Points kept per signal; older points are discarded so memory stays bounded. */
export const LIVE_POINTS = 300;

export type LiveConnection = 'idle' | 'connecting' | 'open' | 'reconnecting';

export interface LivePoint {
  /** Device sample time, epoch milliseconds. */
  readonly time: number;
  readonly value: number;
}

export interface LiveSeries {
  readonly name: string;
  readonly unit: string;
  readonly points: readonly LivePoint[];
}

/** The part of `EventSource` the stream uses. */
export interface EventSourceLike {
  onopen: ((event: Event) => void) | null;
  onerror: ((event: Event) => void) | null;
  addEventListener(type: string, listener: (event: MessageEvent<string>) => void): void;
  close(): void;
}

/** How an `EventSource` is created; tests substitute a fake. */
export const EVENT_SOURCE = new InjectionToken<(url: string) => EventSourceLike>('EVENT_SOURCE', {
  providedIn: 'root',
  factory: () => (url: string) => new EventSource(url) as unknown as EventSourceLike,
});

/**
 * One device's live readings as signals (design D5). Provided per page and closed with it; the
 * browser's own reconnection is used, and an error only marks the stream as reconnecting.
 */
@Injectable()
export class LiveStream {
  private readonly createSource = inject(EVENT_SOURCE);
  private source: EventSourceLike | null = null;
  private readonly buffers = signal<ReadonlyMap<string, LiveSeries>>(new Map());

  readonly connection = signal<LiveConnection>('idle');
  readonly lastReading = signal<LiveReading | null>(null);
  readonly series = computed(() =>
    [...this.buffers().values()].sort((a, b) => a.name.localeCompare(b.name)),
  );

  constructor() {
    inject(DestroyRef).onDestroy(() => this.close());
  }

  /** Streams one device, closing any previous stream and clearing its data. */
  connect(deviceId: string): void {
    this.close();
    this.buffers.set(new Map());
    this.lastReading.set(null);
    this.connection.set('connecting');

    const source = this.createSource(ApiUrls.liveStream(deviceId));
    source.onopen = () => this.connection.set('open');
    source.onerror = () => this.connection.set('reconnecting');
    source.addEventListener('reading', (event) => this.receive(event.data));
    this.source = source;
  }

  close(): void {
    this.source?.close();
    this.source = null;
    this.connection.set('idle');
  }

  private receive(data: string): void {
    let reading: LiveReading;
    try {
      reading = JSON.parse(data) as LiveReading;
    } catch {
      return; // A malformed event is skipped; the next one will arrive.
    }

    const time = Date.parse(reading.timestampDevice);
    if (Number.isNaN(time) || !Array.isArray(reading.readings)) {
      return;
    }

    this.buffers.update((current) => {
      const next = new Map(current);
      for (const signalReading of reading.readings) {
        const previous = next.get(signalReading.name)?.points ?? [];
        const points = [...previous, { time, value: signalReading.value }];
        next.set(signalReading.name, {
          name: signalReading.name,
          unit: signalReading.unit,
          points: points.length > LIVE_POINTS ? points.slice(points.length - LIVE_POINTS) : points,
        });
      }
      return next;
    });
    this.lastReading.set(reading);
    this.connection.set('open');
  }
}
