import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { SignalSeries } from '../../core/api/models';
import { Chart } from '../../shared/charts/chart';
import { History } from './history';

describe('History', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.overrideComponent(Chart, {
      set: { template: '<div class="chart-stub"></div>', imports: [] },
    });
  });

  function render(inputs: Record<string, string>) {
    const fixture = TestBed.createComponent(History);
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    fixture.detectChanges();
    TestBed.tick();
    return {
      fixture,
      http: TestBed.inject(HttpTestingController),
      root: fixture.nativeElement as HTMLElement,
    };
  }

  it('requests the series the URL selects and states the bucket width', async () => {
    const { fixture, http, root } = render({ device: 'dev-1', signal: 'engine_rpm', range: '1h' });

    http.expectOne((r) => r.url.startsWith('/api/devices?')).flush([]);
    http.expectOne('/api/devices/dev-1/signals').flush([
      {
        name: 'engine_rpm',
        value: 1726.25,
        unit: 'rpm',
        sampledAt: '2026-09-29T13:35:11Z',
        processingMode: 'P2',
      },
    ]);
    const request = http.expectOne((r) =>
      r.url.startsWith('/api/devices/dev-1/signals/engine_rpm/series?'),
    );
    const url = new URL(request.request.urlWithParams, 'http://localhost');
    expect(
      Date.parse(url.searchParams.get('to') ?? '') - Date.parse(url.searchParams.get('from') ?? ''),
    ).toBe(3_600_000);
    request.flush({
      deviceId: 'dev-1',
      signal: 'engine_rpm',
      from: url.searchParams.get('from') ?? '',
      to: url.searchParams.get('to') ?? '',
      bucketSeconds: 30,
      points: [
        {
          bucketStart: '2026-09-29T13:00:00Z',
          average: 1800,
          minimum: 900,
          maximum: 2400,
          samples: 30,
        },
      ],
    } satisfies SignalSeries);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root.querySelector('mat-card-title')?.textContent).toContain('Број обртаја мотора');
    expect(root.querySelector('mat-card-subtitle')?.textContent).toContain(
      'интервал агрегације 30 s',
    );
    expect(root.querySelector('.chart-stub')).not.toBeNull();
  });

  it('explains an empty series', async () => {
    const { fixture, http, root } = render({ device: 'dev-1', signal: 'oil_temp' });

    http.expectOne((r) => r.url.startsWith('/api/devices?')).flush([]);
    http.expectOne('/api/devices/dev-1/signals').flush([]);
    http
      .expectOne((r) => r.url.startsWith('/api/devices/dev-1/signals/oil_temp/series?'))
      .flush({
        deviceId: 'dev-1',
        signal: 'oil_temp',
        from: '',
        to: '',
        bucketSeconds: 5,
        points: [],
      });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root.textContent).toContain('Нема узорака овог сигнала');
  });

  it('shows the refusal of an explicit range where it applies', async () => {
    const { fixture, http, root } = render({
      device: 'dev-1',
      signal: 'engine_rpm',
      from: '2026-09-29T12:00:00Z',
      to: '2026-09-29T10:00:00Z',
    });
    const refusal = { detail: "'from' is invalid: 'from' must be earlier than 'to'" };
    const badRequest = { status: 400, statusText: 'Bad Request' };

    // The range-bound requests are refused; the device's signal list does not depend on it.
    http.expectOne((r) => r.url.startsWith('/api/devices?')).flush(refusal, badRequest);
    http.expectOne('/api/devices/dev-1/signals').flush([]);
    http
      .expectOne((r) => r.url.startsWith('/api/devices/dev-1/signals/engine_rpm/series?'))
      .flush(refusal, badRequest);
    await fixture.whenStable();
    fixture.detectChanges();

    const alerts = [...root.querySelectorAll('[role="alert"]')].map((a) => a.textContent);
    expect(alerts.length).toBe(2);
    expect(alerts.every((a) => a?.includes("'from' must be earlier than 'to'"))).toBe(true);
  });
});
