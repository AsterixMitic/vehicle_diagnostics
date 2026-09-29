import { InstantRange } from '../time/range';

/** Relative URLs of the backend's read API; the dev proxy and nginx forward `/api`. */
export const ApiUrls = {
  devices: (range: InstantRange): string => `/api/devices?${query(range)}`,

  latestSignals: (deviceId: string): string => `/api/devices/${segment(deviceId)}/signals`,

  signalSeries: (
    deviceId: string,
    signal: string,
    range: InstantRange,
    bucketSeconds?: number,
  ): string =>
    `/api/devices/${segment(deviceId)}/signals/${segment(signal)}/series?${query(range, bucketSeconds)}`,

  evaluationSummary: (range: InstantRange): string => `/api/evaluation/summary?${query(range)}`,

  evaluationSeries: (range: InstantRange, bucketSeconds?: number): string =>
    `/api/evaluation/series?${query(range, bucketSeconds)}`,

  evaluationExport: (range: InstantRange): string => `/api/evaluation/export.csv?${query(range)}`,

  liveStream: (deviceId: string): string =>
    `/api/stream/readings?${new URLSearchParams({ deviceId }).toString()}`,
} as const;

function segment(value: string): string {
  return encodeURIComponent(value);
}

/** URLSearchParams encodes `+` as `%2B`, so an explicit offset survives the query string. */
function query(range: InstantRange, bucketSeconds?: number): string {
  const params = new URLSearchParams({ from: range.from, to: range.to });
  if (bucketSeconds !== undefined) {
    params.set('bucketSeconds', String(bucketSeconds));
  }
  return params.toString();
}
