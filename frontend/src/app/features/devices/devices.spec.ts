import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { Device } from '../../core/api/models';
import { Devices } from './devices';

describe('Devices', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }),
  );

  function render() {
    const fixture = TestBed.createComponent(Devices);
    fixture.detectChanges();
    return {
      fixture,
      http: TestBed.inject(HttpTestingController),
      root: fixture.nativeElement as HTMLElement,
    };
  }

  it('lists devices seen in the last fifteen minutes with links to their views', async () => {
    const { fixture, http, root } = render();
    TestBed.tick();

    const request = http.expectOne((r) => r.url.startsWith('/api/devices?'));
    const url = new URL(request.request.urlWithParams, 'http://localhost');
    const span =
      Date.parse(url.searchParams.get('to') ?? '') - Date.parse(url.searchParams.get('from') ?? '');
    expect(span).toBe(15 * 60_000);
    request.flush([
      {
        deviceId: 'live-p2-01',
        vehicleId: 'car-01',
        lastSeen: '2026-09-29T13:35:11Z',
        processingMode: 'P2',
      },
    ] satisfies Device[]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root.querySelector('td')?.textContent).toContain('live-p2-01');
    const hrefs = [...root.querySelectorAll('td.links a')].map((a) => a.getAttribute('href'));
    expect(hrefs).toEqual(['/live?device=live-p2-01', '/history?device=live-p2-01']);
    http.verify();
  });

  it('says so when no device sent data', async () => {
    const { fixture, http, root } = render();
    TestBed.tick();

    http.expectOne((r) => r.url.startsWith('/api/devices?')).flush([]);
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root.textContent).toContain('Ниједан уређај');
  });
});
