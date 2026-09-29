/** Shapes of the backend's `/api` responses (add-evaluation-api). Times are ISO 8601 strings. */

export type ProcessingMode = 'P0' | 'P1' | 'P2' | 'P3';

/** Evaluation rows use `unknown` for rejections whose mode could not be determined. */
export type EvaluationMode = ProcessingMode | 'unknown';

export interface Device {
  readonly deviceId: string;
  readonly vehicleId: string;
  readonly lastSeen: string;
  readonly processingMode: ProcessingMode;
}

export interface LatestSignal {
  readonly name: string;
  readonly value: number;
  readonly unit: string;
  readonly sampledAt: string;
  readonly processingMode: ProcessingMode;
}

export interface SignalPoint {
  readonly bucketStart: string;
  readonly average: number;
  readonly minimum: number;
  readonly maximum: number;
  readonly samples: number;
}

export interface SignalSeries {
  readonly deviceId: string;
  readonly signal: string;
  readonly from: string;
  readonly to: string;
  readonly bucketSeconds: number;
  readonly points: readonly SignalPoint[];
}

/** Milliseconds. */
export interface LatencyPercentiles {
  readonly p50: number;
  readonly p95: number;
  readonly p99: number;
}

export interface ModeSummary {
  readonly mode: EvaluationMode;
  readonly received: number;
  readonly persisted: number;
  readonly duplicates: number;
  readonly rejected: number;
  readonly lost: number;
  readonly messagesPerSecond: number;
  readonly bytesPerSecond: number;
  readonly meanPayloadBytes: number | null;
  readonly endToEnd: LatencyPercentiles | null;
  readonly transport: LatencyPercentiles | null;
  readonly backend: LatencyPercentiles | null;
}

export interface RejectionCount {
  readonly mode: EvaluationMode;
  readonly reason: string;
  readonly count: number;
}

export interface EvaluationSummary {
  readonly from: string;
  readonly to: string;
  readonly modes: readonly ModeSummary[];
  readonly rejections: readonly RejectionCount[];
}

export interface ModeSeriesPoint {
  readonly bucketStart: string;
  readonly mode: EvaluationMode;
  readonly received: number;
  readonly messagesPerSecond: number;
  readonly bytesPerSecond: number;
  readonly endToEndP95: number | null;
}

export interface EvaluationSeries {
  readonly from: string;
  readonly to: string;
  readonly bucketSeconds: number;
  readonly points: readonly ModeSeriesPoint[];
}

export interface LiveSignal {
  readonly name: string;
  readonly value: number;
  readonly unit: string;
}

/** One `reading` event of the live stream: a persisted message. */
export interface LiveReading {
  readonly deviceId: string;
  readonly vehicleId: string;
  readonly sequence: number;
  readonly timestampDevice: string;
  readonly processingMode: ProcessingMode;
  readonly readings: readonly LiveSignal[];
}
