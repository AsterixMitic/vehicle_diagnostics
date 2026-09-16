# CAN captures

Recorded CAN traffic used as decoder regression fixtures and as replay input for the
evaluation (architecture.md §18: every configuration runs against the identical capture).

## Format

One frame per line, the `candump -l` format produced by `can-utils` and by USB-CAN adapters:

```
(1694793600.123456) can0 7E8#03410C1AF8
(1694793600.133456) can0 18DAF110#03410D3C0000000000
```

- Timestamp in seconds since the Unix epoch, microsecond resolution.
- Interface name is informational.
- A three-hex-digit identifier is standard (11-bit); an eight-digit one is extended (29-bit).
- Data bytes as hex after `#`, zero to sixteen characters.
- Lines starting with `#` are comments and are skipped.

## Expectations

A fixture `name.log` with a sibling `name.expected.json` is picked up by the decoder tests
automatically. The expectation is a JSON array with one entry per frame, in file order:

```json
[
  { "line": 3, "outcome": "decoded", "name": "engine_rpm", "value": 1726, "unit": "rpm" },
  { "line": 4, "outcome": "ignored", "reason": "negative_response" }
]
```

`line` is the 1-based line number in the log (comments count), so a mismatch points at the frame.

## Provenance

| File | Source | Notes |
|---|---|---|
| `synthetic-obd-mode01.log` | Hand-written | **Synthetic.** Exercises every supported PID and every ignore reason. Not evidence of anything about a real vehicle. |

Real recordings from the vehicle and from the simulated ECU go here with their provenance
(vehicle, date, adapter, bitrate) — only those may be cited in the thesis.
