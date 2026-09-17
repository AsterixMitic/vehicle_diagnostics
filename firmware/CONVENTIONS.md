# Firmware conventions

Conventions for the embedded side of the vehicle-diagnostics platform. Authoritative for
everything under `firmware/`. The backend conventions in the repository root do **not** apply
here, and this file does not apply to the backend.

## Read first

- `../docs/architecture.md` §3 (hardware, pinout, topologies), §7 (firmware layering and tasks),
  §12.1 (patterns), §16 (safety). Single source of truth; propose an ADR rather than deviating.
- `README.md` in this directory for wiring and bring-up.

## One area per change

Never edit backend code in a firmware change, or firmware code in a backend change. The two
have different languages, different toolchains and different review criteria, and mixing them
in one change makes both harder to review.

## Tech stack

| Part | Technology |
|---|---|
| Gateway | C11, ESP-IDF v5.5, FreeRTOS, target `esp32` |
| ECU simulator | C++, Arduino framework, Uno, MCP2515 over SPI |
| Gateway bus controller | TWAI, Espressif's name for the CAN peripheral |
| Host tests | Plain C11, a Makefile, no framework |

## Layering, enforced by the component graph

```
application  →  diagnostics  →  transport  →  link  →  hardware abstraction
```

Each layer depends only on the one directly beneath it, declared in its `CMakeLists.txt` under
`REQUIRES`. A layer that reaches past its neighbour fails to link, which is the point.

**Only the `gw_hal` component may include `driver/*` or `esp_*` headers, and only in its `.c`
files.** Every public header in this tree must compile on a development machine with no
ESP-IDF installed. That is what makes the protocol layers testable without hardware, and it is
not negotiable: a vendor include in a public header silently removes a whole layer from the
host test build.

**Component names share one namespace with ESP-IDF's own.** The hardware abstraction is
`gw_hal`, not `hal`, because ESP-IDF ships a core component called `hal` and a local component
of the same name shadows it. The symptom is baffling: unrelated core components fail with
`fatal error: hal/sha_types.h: No such file or directory`. Before naming a component, check
that `$IDF_PATH/components/<name>` does not already exist.

## C conventions

- C11. Warnings are errors; do not silence one without a comment saying why.
- `snake_case` for functions and variables, component prefix on everything public
  (`hal_can_receive`, `can_link_start`). Types end in `_t`. Macros are `UPPER_SNAKE`.
- Return a typed error enum for expected failures such as timeouts, full buffers and bus
  errors. Reserve `assert` for programmer mistakes that cannot happen in a correct build.
- **No dynamic allocation after start-up**, and none at all on the receive path. Buffers are
  static and their capacity is a compile-time constant. Timing must not depend on heap state.
- Nothing blocks on the receive path. Components hand work over through bounded queues; a slow
  consumer causes a counted drop, never a stall.
- Every loss is counted. A discarded frame, a bus error, a missed deadline: if it can happen,
  a counter says how often it did. These counters are measurements the thesis reports, not
  debugging aids.
- Timestamp a frame at the earliest point it is observed, not when it is processed. Sampling
  jitter is a measured quantity and a late timestamp destroys it.

## Pin assignment

Taken from architecture.md §3. Do not invent alternatives.

| Gateway, ESP32 → SN65HVD230 | Simulator, Uno → MCP2515 |
|---|---|
| `3V3` → VCC, `GND` → GND | `5V` → VCC, `GND` → GND |
| `GPIO5` → CTX | `D10` → CS, `D11` → SI, `D12` → SO, `D13` → SCK |
| `GPIO4` → CRX | `D2` → INT |

Bus runs at 500 kbps, classic CAN 2.0. CAN FD is out of scope for the whole project.

**MCP2515 crystal**: breakout boards ship with 8 MHz or 16 MHz parts. The wrong value gives a
silent bus that looks exactly like bad wiring. The sketch prints the configured value at
start-up; check it against the board before debugging anything else.

## Safety, non-negotiable

- Against a **real vehicle**, read-only services only: OBD-II Mode 01, 03 and 09.
- Never `ECUReset`, never write by identifier, never routine control, never programming, on a
  real vehicle, in any code path, not even behind a flag that defaults to off.
- Every write-capable code path must be guarded by an explicit destination-type check that
  admits only the simulator. Write the guard before the first write-capable path exists, not
  after it.
- The vehicle stays parked during measurement, and the device is unplugged afterwards: pin 16
  is live with the ignition off.
- The `+12 V` line needs a fuse. Do not skip it.

## Commands

```bash
. ~/esp/esp-idf/export.sh                       # once per shell
idf.py -C gateway set-target esp32              # once per checkout
idf.py -C gateway build                         # build the gateway
idf.py -C gateway -p /dev/ttyUSB0 flash monitor # flash and watch

make -C gateway/test/host                       # host unit tests, needs only a C compiler

arduino-cli compile --fqbn arduino:avr:uno sim-ecu
arduino-cli upload  --fqbn arduino:avr:uno -p /dev/ttyACM0 sim-ecu
```

## Working agreement

- Implement exactly what the change at hand describes. No extra services, no
  speculative abstraction, no "while I was here".
- A change is not done until it builds clean and its tests pass. Do not report success without
  running them, and say plainly which tiers ran and which did not.
- Small commits, conventional messages, English throughout. Thesis prose is Serbian and lives
  in `thesis/`; source files are never mixed-language.
