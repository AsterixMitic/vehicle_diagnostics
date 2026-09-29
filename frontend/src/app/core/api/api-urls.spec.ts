import { ApiUrls } from './api-urls';

describe('ApiUrls', () => {
  const range = { from: '2026-09-29T10:00:00.000Z', to: '2026-09-29T11:00:00.000Z' };

  it('puts the range in the query', () => {
    expect(ApiUrls.devices(range)).toBe(
      '/api/devices?from=2026-09-29T10%3A00%3A00.000Z&to=2026-09-29T11%3A00%3A00.000Z',
    );
  });

  it('encodes path segments and the optional bucket', () => {
    expect(ApiUrls.signalSeries('esp32 01', 'engine_rpm', range, 60)).toBe(
      '/api/devices/esp32%2001/signals/engine_rpm/series?from=2026-09-29T10%3A00%3A00.000Z&to=2026-09-29T11%3A00%3A00.000Z&bucketSeconds=60',
    );
  });

  it('encodes a plus sign of an offset', () => {
    expect(
      ApiUrls.evaluationSummary({ from: '2026-09-29T12:00:00+02:00', to: range.to }),
    ).toContain('from=2026-09-29T12%3A00%3A00%2B02%3A00');
  });

  it('filters the live stream by device', () => {
    expect(ApiUrls.liveStream('live-p2-01')).toBe('/api/stream/readings?deviceId=live-p2-01');
  });
});
