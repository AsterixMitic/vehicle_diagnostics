import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { EvaluationSeries, EvaluationSummary } from '../../core/api/models';
import { Chart } from '../../shared/charts/chart';
import { Evaluation } from './evaluation';

const SUMMARY: EvaluationSummary = {
  from: '2026-09-29T13:34:53Z',
  to: '2026-09-29T13:36:55Z',
  modes: [
    {
      mode: 'P0',
      received: 60,
      persisted: 60,
      duplicates: 0,
      rejected: 0,
      lost: 0,
      messagesPerSecond: 1.0009,
      bytesPerSecond: 403.1,
      meanPayloadBytes: 402.78,
      endToEnd: { p50: 10.1515, p95: 15.0576, p99: 70.3044 },
      transport: { p50: 0.9345, p95: 1.5533, p99: 22.4474 },
      backend: { p50: 9.3305, p95: 13.3571, p99: 48.6452 },
    },
    {
      mode: 'P2',
      received: 61,
      persisted: 61,
      duplicates: 0,
      rejected: 0,
      lost: 0,
      messagesPerSecond: 1,
      bytesPerSecond: 396.11,
      meanPayloadBytes: 396.11,
      endToEnd: { p50: 6.006, p95: 8.323, p99: 8.9914 },
      transport: { p50: 0.781, p95: 1.253, p99: 1.6836 },
      backend: { p50: 5.179, p95: 7.039, p99: 7.742 },
    },
    {
      mode: 'unknown',
      received: 2,
      persisted: 0,
      duplicates: 0,
      rejected: 2,
      lost: 0,
      messagesPerSecond: 0.017,
      bytesPerSecond: 0.3,
      meanPayloadBytes: 20,
      endToEnd: null,
      transport: null,
      backend: null,
    },
  ],
  rejections: [{ mode: 'unknown', reason: 'unknown_topic', count: 2 }],
};

describe('Evaluation', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.overrideComponent(Chart, {
      set: { template: '<div class="chart-stub"></div>', imports: [] },
    });
  });

  async function render() {
    const fixture = TestBed.createComponent(Evaluation);
    fixture.componentRef.setInput('range', '1h');
    fixture.detectChanges();
    TestBed.tick();
    const http = TestBed.inject(HttpTestingController);
    const summary = http.expectOne((r) => r.url.startsWith('/api/evaluation/summary?'));
    summary.flush(SUMMARY);
    http
      .expectOne((r) => r.url.startsWith('/api/evaluation/series?'))
      .flush({
        from: SUMMARY.from,
        to: SUMMARY.to,
        bucketSeconds: 30,
        points: [
          {
            bucketStart: '2026-09-29T13:35:00Z',
            mode: 'P0',
            received: 30,
            messagesPerSecond: 1,
            bytesPerSecond: 403,
            endToEndP95: 15,
          },
          {
            bucketStart: '2026-09-29T13:35:00Z',
            mode: 'P2',
            received: 30,
            messagesPerSecond: 1,
            bytesPerSecond: 396,
            endToEndP95: 8.3,
          },
        ],
      } satisfies EvaluationSeries);
    await fixture.whenStable();
    fixture.detectChanges();
    return {
      root: fixture.nativeElement as HTMLElement,
      summaryUrl: summary.request.urlWithParams,
    };
  }

  it('has one table row per mode with Serbian-formatted metrics', async () => {
    const { root } = await render();

    const rows = [...root.querySelectorAll('table.summary tbody tr')];
    expect(rows.map((r) => r.querySelector('th')?.textContent?.trim())).toEqual([
      'P0',
      'P2',
      'непознат',
    ]);
    const p0 = [...rows[0].querySelectorAll('td')].map((td) => td.textContent?.trim());
    expect(p0.slice(0, 8)).toEqual(['60', '60', '0', '0', '0', '1,001', '403,100', '403']);
    expect(p0[8]).toBe('10,15');
  });

  it('shows absent latencies as absent, not as zero', async () => {
    const { root } = await render();

    const unknown = [...root.querySelectorAll('table.summary tbody tr')][2];
    const latencies = [...unknown.querySelectorAll('td')]
      .slice(8)
      .map((td) => td.textContent?.trim());
    expect(latencies).toEqual(Array(9).fill('—'));
  });

  it('draws the percentile chart and three charts over time', async () => {
    const { root } = await render();

    expect(root.querySelectorAll('.chart-stub').length).toBe(4);
    expect(root.textContent).toContain('интервал 30 s');
  });

  it('lists rejections and offers the export of the same range', async () => {
    const { root, summaryUrl } = await render();

    expect(root.querySelector('table.rejections tbody td code')?.textContent).toBe('unknown_topic');
    const href = root.querySelector('a[download]')?.getAttribute('href') ?? '';
    expect(href.startsWith('/api/evaluation/export.csv?')).toBe(true);
    expect(href.split('?')[1]).toBe(summaryUrl.split('?')[1]);
  });
});
