# Firmware

Embedded side of the vehicle-diagnostics platform: the ESP32 gateway and the Arduino ECU
simulator. Conventions are in `CONVENTIONS.md`; the system design is in `../docs/architecture.md`.

Current scope: the gateway reads OBD-II Mode 01 current data from an engine ECU, the bench
simulator or a real vehicle, decodes it exactly as the backend does and prints every reading on
the console. With a WiFi SSID configured it also publishes every poll cycle to the backend over
MQTT, as decoded readings (mode P2) or as the raw answer frames (mode P0), switchable at runtime.

## Layout

```
firmware/
├── CONVENTIONS.md                     conventions, safety rules, pin assignment
├── gateway/                      ESP32, ESP-IDF v5.5
│   ├── components/gw_hal/        the ONLY place vendor headers may appear
│   ├── components/can_link/      receive task, ring buffer, bus health
│   ├── components/isotp/         ISO 15765-2 transport, no I/O
│   ├── components/obd/           Mode 01 table, decoder, support bitmaps, scheduler, guard
│   ├── components/envelope/      version-1 JSON envelope from one poll cycle, no I/O
│   ├── components/net/           WiFi, SNTP, MQTT client, persisted message sequence
│   ├── main/                     the application and its build settings (Kconfig.projbuild)
│   └── test/
│       ├── shared/               test sources, used by both test builds
│       ├── host/                 runs on a development machine, needs only a C compiler
│       └── target/               runs on an ESP32, real or emulated
└── sim-ecu/                      Arduino Uno + MCP2515
    ├── vehicle_model.*           drive cycle and dynamics, no Arduino dependency
    ├── obd_responder.*           Mode 01 answers and support bitmaps, no Arduino dependency
    └── test/host/                host tests, round trip through the gateway's decoder
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
make -C sim-ecu/test/host                  # simulator model and responder, host only

arduino-cli compile --fqbn arduino:avr:uno sim-ecu
```

The emulator run prints `RESULT: PASS` followed by `TESTS COMPLETE`, then halts. It does not
exit on its own, so stop it with Ctrl-C or run it under `timeout`.

The OBD decoder test reads the backend's capture fixture, `experiments/captures/synthetic-obd-mode01.*`:
from the repository on the host, embedded in the binary on the target. Both decoders pass the
same file, which is what keeps a P0 reading decoded on the backend and a P2 reading decoded on
the gateway identical.

### Two gateway builds

The build settings live in `gateway/main/Kconfig.projbuild` (`idf.py menuconfig`, menu *Vehicle
diagnostics gateway*): target (bench or vehicle), listen-only, bit rate (250 or 500 kbps),
functional addressing, poll period and the error-guard threshold. The defaults are the safe
ones for a car: target vehicle, listen-only, 500 kbps. Keep the two variants in their own build
directories, each with its own `sdkconfig`:

```bash
cd gateway
# Vehicle, the defaults: listen-only, nothing is transmitted
idf.py -B build-vehicle -D SDKCONFIG=build-vehicle/sdkconfig build

# Bench: the simulator on the desk, requests on, automatic bus-off recovery
idf.py -B build-bench -D SDKCONFIG=build-bench/sdkconfig \
       -D SDKCONFIG_DEFAULTS="sdkconfig.defaults;sdkconfig.bench" build
```

To poll a vehicle, open `idf.py -B build-vehicle -D SDKCONFIG=build-vehicle/sdkconfig menuconfig`
and clear *Listen only* (and set the bit rate if the car is not on 500 kbps); keep the target on
*Vehicle*, so the error guard stays armed.

## Publishing to the backend

Set the *Publishing to the backend* submenu in `menuconfig` (WiFi SSID and password, broker URI,
device and vehicle id, start-up mode). The values land in the build directory's `sdkconfig`,
which git ignores; never put credentials in `sdkconfig.defaults` or `sdkconfig.bench`. With an
empty SSID nothing is published and the gateway behaves exactly as before.

```bash
idf.py -B build-bench -D SDKCONFIG=build-bench/sdkconfig menuconfig   # Vehicle diagnostics gateway → Publishing
idf.py -B build-bench -D SDKCONFIG=build-bench/sdkconfig -p /dev/ttyUSB1 -b 115200 flash monitor
```

The broker of the Compose stack listens on localhost only. For the demo, publish it on the
laptop's WiFi address with the opt-in override, and point the gateway at that address
(`ip -4 addr show` gives it):

```bash
cd deploy/compose
docker compose -f docker-compose.yml -f docker-compose.lan.yml up -d mosquitto
```

Every 5 s the console adds a `publish |` line: mode, connection, clock, messages handed over and
acknowledged, and every drop by reason (`offline`, `unsynced` before SNTP sets the clock,
`refused`). The dashboard shows the device under its id, live and in history.

Switch the mode at runtime with a retained command, so it survives reconnects:

```bash
docker exec vehiclediagnostics-mosquitto-1 mosquitto_pub -r -q 1 \
    -t veh/bench-01/dev/gateway-01/cmd/req -m '{"processingMode":"P0"}'
```

Without hardware, the software-in-the-loop bench prints the same envelopes and can feed them
to the running stack:

```bash
make -C tools/sil
tools/sil/build/sil 60 --envelopes P2 --realtime | docker exec -i vehiclediagnostics-mosquitto-1 \
    mosquitto_pub -t veh/sil-car/dev/sil-01/tel/signals -q 1 -l
```

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

## Bench procedure

Serial ports belong to the `dialout` group: `sudo usermod -aG dialout $USER`, then log in again.
Clone Unos with a CH340 and most ESP32 boards appear as `/dev/ttyUSB*`; `ls /dev/serial/by-id/`
tells them apart. If flashing the ESP32 stops after the baud rate change, flash at `-b 115200`.

```bash
arduino-cli compile --fqbn arduino:avr:uno --upload -p /dev/ttyUSB0 sim-ecu
arduino-cli monitor -p /dev/ttyUSB0 --config baudrate=115200

cd gateway
idf.py -B build-bench -D SDKCONFIG=build-bench/sdkconfig -p /dev/ttyUSB1 -b 115200 flash monitor
```

The gateway first asks the simulator for its support bitmaps and logs
`discovered 14 supported PIDs`, then polls them once a second. Every reading is one line, and the
counters follow every 5 s:

```
OBD,12345,0x0C,engine_rpm,1726.00,rpm,0x7E8
I (17312) gateway: stats | cycles=12 requests=171 answers=171 timeouts=0 negative=0 ...
```

Record a run with `idf.py ... monitor | tee bench.log`. The simulator prints each request and
answer, and every 5 s its model state (engine speed, road speed, gear, coolant). The values
follow a 120 s drive cycle: idle, accelerate through the gears, cruise, brake.

If the gateway counts only timeouts, the simulator is not answering: check its console for
`MCP2515 ready`, the crystal setting and the wiring. A bench build recovers from bus-off by
itself and resumes polling.

## First contact with a vehicle

Only after the bench procedure works. Read `CONVENTIONS.md` for the safety rules and
`../docs/architecture.md` §3 for topology B. The gateway sends nothing but Mode 01 reads.

1. **Protocol.** Plug in the ELM327 first and ask `ATDP` (or let a scanner app show it). Only
   *ISO 15765-4 CAN, 11-bit ID* at 500 or 250 kbps is supported in this increment.
2. **Termination off.** The vehicle bus is already terminated at both ends; remove or disable
   the 120 Ω resistor on the SN65HVD230 module. With the car off, CAN-H to CAN-L at the port
   should read about 60 Ω, and still about 60 Ω with the gateway attached.
3. **Power.** Run the ESP32 from the laptop's USB with the laptop **on battery**, not on its
   charger, to avoid a ground loop through the car. Pin 16 (+12 V, live with the ignition off)
   needs a fuse and a buck converter before it may power anything.
4. **Listen first.** Flash the default vehicle build. It transmits and acknowledges nothing and
   prints, every 5 s, the identifiers it sees (`ID,std,0x7E8,…`) and the bus error count. A
   silent port is normal on many cars, since the diagnostic gateway only answers requests; rising
   bus errors mean a wrong bit rate: stop and check step 1.
5. **Then poll.** Ignition on, vehicle parked. Rebuild with *Listen only* cleared and the target
   still *Vehicle*, flash, and record: `idf.py -B build-vehicle -D SDKCONFIG=build-vehicle/sdkconfig
   -p /dev/ttyUSB0 -b 115200 flash monitor | tee car.log`. If the bus errors pass the threshold
   or the controller goes bus-off, the gateway stops all requests, says why, and listens until
   reset. If discovery fails, try *functional addressing*.
6. **Compare** the logged values with the ELM327 or a scanner app while the engine idles.
7. **Unplug** the gateway when done.
