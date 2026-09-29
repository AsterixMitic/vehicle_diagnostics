import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { LatestSignal } from '../../core/api/models';
import { EVENT_SOURCE, EventSourceLike } from '../../core/live/live-stream';
import { Chart } from '../../shared/charts/chart';
import { Live } from './live';

class FakeEventSource implements EventSourceLike {
  onopen: ((event: Event) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  closed = false;
  private listener: ((event: MessageEvent<string>) => void) | null = null;

  constructor(readonly url: string) {}

  addEventListener(_type: string, listener: (event: MessageEvent<string>) => void): void {
    this.listener = listener;
  }

  close(): void {
    this.closed = true;
  }

  emit(value: number): void {
    const data = JSON.stringify({
      deviceId: 'dev-1',
      vehicleId: 'car-01',
      sequence: 7,
      timestampDevice: '2026-09-29T13:35:12Z',
      processingMode: 'P2',
      readings: [{ name: 'engine_rpm', value, unit: 'rpm' }],
    });
    this.listener?.(new MessageEvent('reading', { data }));
  }
}

describe('Live', () => {
  let sources: FakeEventSource[];

  beforeEach(() => {
    sources = [];
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
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
    // Charts draw on a canvas the test DOM does not have.
    TestBed.overrideComponent(Chart, {
      set: { template: '<div class="chart-stub"></div>', imports: [] },
    });
  });

  async function render() {
    const fixture = TestBed.createComponent(Live);
    fixture.componentRef.setInput('device', 'dev-1');
    fixture.detectChanges();
    TestBed.tick();
    const http = TestBed.inject(HttpTestingController);
    http
      .expectOne((r) => r.url.startsWith('/api/devices?'))
      .flush([
        {
          deviceId: 'dev-1',
          vehicleId: 'car-01',
          lastSeen: '2026-09-29T13:35:11Z',
          processingMode: 'P2',
        },
      ]);
    http.expectOne('/api/devices/dev-1/signals').flush([
      {
        name: 'engine_rpm',
        value: 1726.25,
        unit: 'rpm',
        sampledAt: '2026-09-29T13:35:11Z',
        processingMode: 'P2',
      },
    ] satisfies LatestSignal[]);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('shows the stored latest values before the stream delivers', async () => {
    const { root } = await render();

    expect(root.querySelector('.value-card .name')?.textContent).toBe('Број обртаја мотора');
    expect(root.querySelector('.value-card .value')?.textContent).toContain('1.726,25');
  });

  it('streams the selected device and updates values and charts as readings arrive', async () => {
    const { fixture, root } = await render();

    expect(sources[0].url).toBe('/api/stream/readings?deviceId=dev-1');
    sources[0].onopen?.(new Event('open'));
    sources[0].emit(2000);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root.querySelector('.value-card .value')?.textContent).toContain('2.000');
    expect(root.querySelectorAll('.chart-stub').length).toBe(1);
    expect(root.querySelector('[role="status"]')?.textContent).toBe('уживо');
  });

  it('shows that it is reconnecting when the stream drops', async () => {
    const { fixture, root } = await render();

    sources[0].onerror?.(new Event('error'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root.querySelector('[role="status"]')?.textContent).toBe('поновно повезивање…');
  });

  it('closes the stream when the page is left', async () => {
    const { fixture } = await render();

    fixture.destroy();

    expect(sources[0].closed).toBe(true);
  });
});
