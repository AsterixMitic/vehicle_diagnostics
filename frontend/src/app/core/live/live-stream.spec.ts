import { Injector } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { EVENT_SOURCE, EventSourceLike, LIVE_POINTS, LiveStream } from './live-stream';

class FakeEventSource implements EventSourceLike {
  onopen: ((event: Event) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  closed = false;
  private readonly listeners = new Map<string, (event: MessageEvent<string>) => void>();

  constructor(readonly url: string) {}

  addEventListener(type: string, listener: (event: MessageEvent<string>) => void): void {
    this.listeners.set(type, listener);
  }

  close(): void {
    this.closed = true;
  }

  open(): void {
    this.onopen?.(new Event('open'));
  }

  fail(): void {
    this.onerror?.(new Event('error'));
  }

  emit(sequence: number, value: number, second = sequence): void {
    const data = JSON.stringify({
      deviceId: 'dev-1',
      vehicleId: 'car-01',
      sequence,
      timestampDevice: new Date(Date.UTC(2026, 8, 29, 10, 0, second)).toISOString(),
      processingMode: 'P2',
      readings: [
        { name: 'engine_rpm', value, unit: 'rpm' },
        { name: 'coolant_temp', value: 80, unit: 'degC' },
      ],
    });
    this.listeners.get('reading')?.(new MessageEvent('reading', { data }));
  }

  emitRaw(data: string): void {
    this.listeners.get('reading')?.(new MessageEvent('reading', { data }));
  }
}

describe('LiveStream', () => {
  let sources: FakeEventSource[];
  let injector: Injector;
  let stream: LiveStream;

  beforeEach(() => {
    sources = [];
    TestBed.configureTestingModule({
      providers: [
        LiveStream,
        {
          provide: EVENT_SOURCE,
          useValue: (url: string) => {
            const source = new FakeEventSource(url);
            sources.push(source);
            return source;
          },
        },
      ],
    });
    injector = TestBed.inject(Injector);
    stream = injector.get(LiveStream);
  });

  it('opens a stream filtered by device and reports its connection state', () => {
    stream.connect('dev-1');

    expect(sources[0].url).toBe('/api/stream/readings?deviceId=dev-1');
    expect(stream.connection()).toBe('connecting');
    sources[0].open();
    expect(stream.connection()).toBe('open');
    sources[0].fail();
    expect(stream.connection()).toBe('reconnecting');
  });

  it('collects every signal of every reading, sorted by name', () => {
    stream.connect('dev-1');
    sources[0].emit(1, 900);
    sources[0].emit(2, 950);

    const series = stream.series();
    expect(series.map((s) => s.name)).toEqual(['coolant_temp', 'engine_rpm']);
    expect(series[1].points.map((p) => p.value)).toEqual([900, 950]);
    expect(series[1].unit).toBe('rpm');
    expect(stream.lastReading()?.sequence).toBe(2);
  });

  it(`keeps only the latest ${LIVE_POINTS} points per signal`, () => {
    stream.connect('dev-1');
    for (let i = 1; i <= LIVE_POINTS + 20; i++) {
      sources[0].emit(i, i, i % 60);
    }

    const rpm = stream.series().find((s) => s.name === 'engine_rpm');
    expect(rpm?.points.length).toBe(LIVE_POINTS);
    expect(rpm?.points[0].value).toBe(21);
    expect(rpm?.points[LIVE_POINTS - 1].value).toBe(LIVE_POINTS + 20);
  });

  it('skips malformed events', () => {
    stream.connect('dev-1');
    sources[0].emitRaw('not json');
    sources[0].emitRaw('{"timestampDevice":"never","readings":[]}');

    expect(stream.series()).toEqual([]);
  });

  it('closes the previous stream and clears its data when the device changes', () => {
    stream.connect('dev-1');
    sources[0].emit(1, 900);

    stream.connect('dev-2');

    expect(sources[0].closed).toBe(true);
    expect(sources[1].url).toContain('deviceId=dev-2');
    expect(stream.series()).toEqual([]);
  });

  it('closes the stream when its injector is destroyed', () => {
    stream.connect('dev-1');

    TestBed.resetTestingModule();

    expect(sources[0].closed).toBe(true);
  });
});
