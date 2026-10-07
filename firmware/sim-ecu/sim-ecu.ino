/**
 * @file sim-ecu.ino
 * @brief Simulated engine ECU: answers OBD-II Mode 01 requests from a vehicle model.
 *
 * Hardware: Arduino Uno with an MCP2515 breakout (TJA1050 transceiver) on SPI.
 * Wiring (architecture.md §3): D10 CS, D11 SI, D12 SO, D13 SCK, D2 INT, 5V, GND.
 *
 * The sketch only moves frames between the MCP2515 and two framework-free pieces that test
 * on a development machine: the vehicle model (vehicle_model.cpp) and the Mode 01 responder
 * (obd_responder.cpp). Requests arrive on 0x7DF or 0x7E0, answers leave on 0x7E8. DTCs, UDS
 * sessions and fault injection are later increments.
 *
 * SAFETY: this device is a bench simulator. Nothing here may ever be pointed at a real
 * vehicle, and no write-capable service exists in this file.
 */
#include <ACAN2515.h>
#include <SPI.h>

#include "obd_responder.h"
#include "vehicle_model.h"

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
    if (!request.ext && obd_sim::respond(model.state(), request.id, request.data, request.len, &answer)) {
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
    const VehicleState &s = model.state();
    Serial.print(F("received "));
    Serial.print(framesReceived);
    Serial.print(F(", answered "));
    Serial.print(framesAnswered);
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
