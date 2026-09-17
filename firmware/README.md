# Firmware

Embedded side of the vehicle-diagnostics platform: the ESP32 gateway and the Arduino ECU
simulator. Conventions are in `CONVENTIONS.md`; the system design is in `../docs/architecture.md`.

Phase F0 scope: prove a CAN frame crosses the bench in both directions. No transport protocol,
no diagnostics, no networking. Those are phases F1 and later.

## Layout

```
firmware/
├── CONVENTIONS.md                     conventions, safety rules, pin assignment
├── gateway/                      ESP32, ESP-IDF v5.5
│   ├── components/gw_hal/        the ONLY place vendor headers may appear
│   ├── components/can_link/      receive task, ring buffer, bus health
│   ├── main/                     phase F0 application
│   └── test/
│       ├── shared/               test sources, used by both test builds
│       ├── host/                 runs on a development machine, needs only a C compiler
│       └── target/               runs on an ESP32, real or emulated
└── sim-ecu/                      Arduino Uno + MCP2515
```

## Toolchain

```bash
# ESP-IDF v5.5, user-local, no administrator rights needed
git clone -b v5.5 --depth 1 --recursive --shallow-submodules \
    https://github.com/espressif/esp-idf.git ~/esp/esp-idf
~/esp/esp-idf/install.sh esp32
python3 ~/esp/esp-idf/tools/idf_tools.py install qemu-xtensa   # to run tests without hardware

# Arduino CLI for the simulator
curl -fsSL https://raw.githubusercontent.com/arduino/arduino-cli/master/install.sh \
    | BINDIR=$HOME/.local/bin sh
arduino-cli core install arduino:avr
arduino-cli lib install ACAN2515
```

On a distribution whose Python ships without `pip`, `install.sh` fails while creating its
virtual environment. Create it manually, then re-run the tool installer:

```bash
python3 -m venv --without-pip ~/.espressif/python_env/idf5.5_py3.14_env
curl -sSL https://bootstrap.pypa.io/get-pip.py | ~/.espressif/python_env/idf5.5_py3.14_env/bin/python -
python3 ~/esp/esp-idf/tools/idf_tools.py install cmake ninja
python3 ~/esp/esp-idf/tools/idf_tools.py install-python-env
```

## Build and test

```bash
. ~/esp/esp-idf/export.sh                  # once per shell

idf.py -C gateway set-target esp32         # once per checkout
idf.py -C gateway build                    # gateway firmware

idf.py -C gateway/test/target set-target esp32
idf.py -C gateway/test/target build
idf.py -C gateway/test/target qemu         # unit tests on an emulated ESP32, no hardware

make -C gateway/test/host                  # same tests on a development machine

arduino-cli compile --fqbn arduino:avr:uno sim-ecu
```

The emulator run prints `RESULT: PASS` followed by `TESTS COMPLETE`, then halts. It does not
exit on its own, so stop it with Ctrl-C or run it under `timeout`.

## Wiring, bench topology A

| ESP32 | SN65HVD230 | | Arduino Uno | MCP2515 |
|---|---|---|---|---|
| `3V3` | VCC | | `5V` | VCC |
| `GND` | GND | | `GND` | GND |
| `GPIO5` | CTX | | `D10` | CS |
| `GPIO4` | CRX | | `D11` | SI |
| | | | `D12` | SO |
| | | | `D13` | SCK |
| | | | `D2` | INT |

Join CAN-H to CAN-H and CAN-L to CAN-L with a twisted pair. **Common ground between the two
boards is mandatory**; without it the bus appears dead in a way that looks like a firmware
fault. Leave the 120 Ω termination enabled at both ends, giving about 60 Ω across the pair.

Three checks before suspecting the code, in this order:

1. **Resistance.** Roughly 60 Ω between CAN-H and CAN-L with everything powered off. A reading
   near 120 Ω means only one end is terminated; near zero means a short.
2. **Crystal.** MCP2515 breakouts carry an 8 MHz or a 16 MHz part. The sketch prints the value
   it was compiled with; if it disagrees with the can on your board, fix
   `QUARTZ_FREQUENCY` in `sim-ecu/sim-ecu.ino`. A mismatch gives a silent bus that looks
   exactly like bad wiring, and it is the most common bring-up failure.
3. **Common ground.** Confirm continuity between the two boards' grounds.

## Bring-up procedure

```bash
idf.py -C gateway -p /dev/ttyUSB0 flash monitor
arduino-cli upload --fqbn arduino:avr:uno -p /dev/ttyACM0 sim-ecu
arduino-cli monitor -p /dev/ttyACM0 --config baudrate=115200
```

The gateway sends a request once a second and prints every frame it receives with its counters.
The simulator prints each request it receives and each answer it sends.

**Phase F0 is met when both consoles show traffic**: the simulator logging `RX 0x7DF` and
`TX 0x7E8`, and the gateway logging `RX std 0x7E8` followed by the round-trip confirmation.
That is a frame sent and received in both directions, which is the phase's exit criterion.

If the gateway reports `transmit timed out`, no other powered node is acknowledging. On CAN a
transmitter needs at least one other active node, so this points at the simulator being
unpowered, the wiring, or the crystal, not at the gateway.

## Working in a real vehicle

Do not, until the bench round trip works. Then read `CONVENTIONS.md` for the safety rules and
`../docs/architecture.md` §3 for topology B. Two differences matter: the termination on the
transceiver module must be **off**, because the vehicle bus is already terminated at both ends,
and the `+12 V` line from pin 16 needs a fuse and a buck converter. Read-only services only.
