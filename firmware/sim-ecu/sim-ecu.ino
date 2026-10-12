/**
 * @file sim-ecu.ino
 * @brief Simulated engine ECU: answers OBD-II Mode 01 requests like a car driving in town.
 *
 * Hardware: Arduino Uno with an MCP2515 breakout (TJA1050 transceiver) on SPI.
 * Wiring (architecture.md §3): D10 CS, D11 SI, D12 SO, D13 SCK, D2 INT, 5V, GND.
 *
 * The sketch only moves frames between the MCP2515 and framework-free pieces that test on a
 * development machine: the Mode 01 responder (obd_responder.cpp) and its source of values.
 * By default the values come from the vehicle model (vehicle_model.cpp): a 180 s city loop
 * with stops, gears, warm-up, overrun and 14 PIDs whose values move together like a petrol
 * hatchback's, calibrated on a recorded drive but a few hundred bytes of flash instead of a
 * recording. REPLAY_RECORDED_DRIVE = true answers instead with the bytes a real car (the
 * Mitsubishi ASX) sent second by second (drive_replay.cpp, drive_profile.cpp, 10 PIDs).
 * Requests arrive on 0x7DF or 0x7E0, answers leave on 0x7E8. DTCs, UDS sessions and fault
 * injection are later increments.
 *
 * SAFETY: this device is a bench simulator. Nothing here may ever be pointed at a real
 * vehicle, and no write-capable service exists in this file.
 */
#include <ACAN2515.h>
#include <SPI.h>

#include "drive_replay.h"
#include "obd_responder.h"
#include "vehicle_model.h"

// true: answer from the recorded drive (drive_profile.cpp); false: from the vehicle model.
static const bool REPLAY_RECORDED_DRIVE = false;

// 0: answer every request at once. Above 0: ignore a request that arrives within this many
// milliseconds of the previous answer, as the recorded Fiat Grande Punto does. A bench for a
// gateway's request spacing; 0 is the normal setting.
static const uint16_t MIN_REQUEST_GAP_MS = 0;

// ---------------------------------------------------------------------------------------
// CHECK THIS FIRST. MCP2515 breakout boards ship with either an 8 MHz or a 16 MHz crystal.
// The wrong value produces a silent bus that looks exactly like bad wiring, and it is the
// single most common cause of a bench that will not come up. Read the can on your board.
// ---------------------------------------------------------------------------------------
static const uint32_t QUARTZ_FREQUENCY = 8UL * 1000UL * 1000UL;  // 8 MHz. Use 16 for a 16 MHz board.

static const uint32_t BUS_BITRATE = 500UL * 1000UL;  // 500 kbps, the diagnostic port rate.

static const byte MCP2515_CS_PIN = 10;
static const byte MCP2515_INT_PIN = 2;

static ACAN2515 can(MCP2515_CS_PIN, SPI, MCP2515_INT_PIN);
static VehicleModel model;

static uint32_t framesReceived = 0;
static uint32_t framesAnswered = 0;
static uint32_t lastReportMs = 0;
static uint32_t lastModelMs = 0;
static uint32_t lastAnswerMs = 0;

static void printFrame(const char *direction, const CANMessage &frame) {
  Serial.print(direction);
  Serial.print(F(" 0x"));
  Serial.print(frame.id, HEX);
  Serial.print(F(" ["));
  Serial.print(frame.len);
  Serial.print(F("]"));
  for (uint8_t i = 0; i < frame.len; i++) {
    Serial.print(frame.data[i] < 0x10 ? F(" 0") : F(" "));
    Serial.print(frame.data[i], HEX);
  }
  Serial.println();
}

void setup() {
  Serial.begin(115200);
  while (!Serial) {
    ;  // Wait for the console on boards that need it.
  }

  Serial.println();
  Serial.println(F("simulated ECU, phase F0 bring-up"));
  Serial.println(REPLAY_RECORDED_DRIVE ? F("source: recorded drive") : F("source: vehicle model, city loop"));
  Serial.print(F("crystal configured as "));
  Serial.print(QUARTZ_FREQUENCY / 1000000UL);
  Serial.println(F(" MHz - if the bus stays silent, check this against the board first"));
  Serial.print(F("bitrate "));
  Serial.print(BUS_BITRATE / 1000UL);
  Serial.println(F(" kbps"));

  SPI.begin();

  ACAN2515Settings settings(QUARTZ_FREQUENCY, BUS_BITRATE);
  // Default settings accept every identifier, which is what a bench wants.
  const uint16_t errorCode = can.begin(settings, [] { can.isr(); });

  if (errorCode == 0) {
    Serial.println(F("MCP2515 ready, answering Mode 01 on 0x7DF and 0x7E0 from 0x7E8"));
  } else {
    Serial.print(F("MCP2515 did NOT start, error 0x"));
    Serial.println(errorCode, HEX);
    Serial.println(F("check: SPI wiring, 5V supply, crystal frequency above, INT on D2"));
  }
}

void loop() {
  const uint32_t now = millis();
  model.advance(now - lastModelMs);
  lastModelMs = now;

  CANMessage request;
  if (can.receive(request)) {
    framesReceived++;

    obd_sim::Frame answer;
    const bool answered = REPLAY_RECORDED_DRIVE
        ? obd_sim::respond(obd_sim::ReplaySource(now), request.id, request.data, request.len, &answer)
        : obd_sim::respond(model.state(), request.id, request.data, request.len, &answer);
    const bool tooSoon = MIN_REQUEST_GAP_MS > 0 && framesAnswered > 0 && now - lastAnswerMs < MIN_REQUEST_GAP_MS;
    if (!request.ext && answered && !tooSoon) {
      CANMessage response;
      response.id = answer.id;
      response.ext = false;
      response.len = answer.dlc;
      for (uint8_t i = 0; i < answer.dlc; i++) {
        response.data[i] = answer.data[i];
      }
      // Answer first, log afterwards: the console must never delay the bus.
      if (can.tryToSend(response)) {
        framesAnswered++;
        lastAnswerMs = now;
        printFrame("RX", request);
        printFrame("TX", response);
      } else {
        Serial.println(F("TX queue full, answer dropped"));
      }
    } else {
      printFrame("RX", request);
    }
  }

  if (now - lastReportMs >= 5000UL) {
    lastReportMs = now;
    Serial.print(F("received "));
    Serial.print(framesReceived);
    Serial.print(F(", answered "));
    Serial.print(framesAnswered);
    // The MCP2515's own view of the bus. Receive errors that grow while nothing is received:
    // frames arrive but cannot be read (bitrate or crystal). All zero and nothing received:
    // no frame reaches the transceiver at all (CAN-H/CAN-L, ground, the other node silent).
    Serial.print(F(" | bus rx_err "));
    Serial.print(can.receiveErrorCounter());
    Serial.print(F(" tx_err "));
    Serial.print(can.transmitErrorCounter());
    Serial.print(F(" eflg 0x"));
    Serial.print(can.errorFlagRegister(), HEX);
    if (REPLAY_RECORDED_DRIVE) {
      const obd_sim::ReplaySource drive(now);
      uint8_t rpm[2] = {0, 0};
      uint8_t speed[1] = {0};
      (void)drive.encode(0x0C, rpm);
      (void)drive.encode(0x0D, speed);
      Serial.print(F(" | drive second "));
      Serial.print(drive.second());
      Serial.print(F(" rpm "));
      Serial.print(((uint16_t)rpm[0] << 8 | rpm[1]) / 4);
      Serial.print(F(" km/h "));
      Serial.println(speed[0]);
    } else {
      const VehicleState &s = model.state();
      Serial.print(F(" | rpm "));
      Serial.print(s.engine_rpm, 0);
      Serial.print(F(" km/h "));
      Serial.print(s.speed_kmh, 1);
      Serial.print(F(" gear "));
      Serial.print(s.gear);
      Serial.print(F(" coolant "));
      Serial.println(s.coolant_c, 1);
    }
  }
}
