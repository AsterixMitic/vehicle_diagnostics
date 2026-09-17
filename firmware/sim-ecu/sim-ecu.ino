/**
 * @file sim-ecu.ino
 * @brief Simulated ECU, phase F0: answer the gateway's request so the bench proves both
 *        directions of the bus.
 *
 * Hardware: Arduino Uno with an MCP2515 breakout (TJA1050 transceiver) on SPI.
 * Wiring (architecture.md §3): D10 CS, D11 SI, D12 SO, D13 SCK, D2 INT, 5V, GND.
 *
 * This increment answers one identifier with one fixed frame. It is deliberately not an
 * OBD-II implementation: services, DTCs, UDS sessions and fault injection are phase F1 and
 * later. The identifiers used here are the real diagnostic ones, so the wiring proven now
 * stays valid when a protocol is layered on top.
 *
 * SAFETY: this device is a bench simulator. Nothing here may ever be pointed at a real
 * vehicle, and no write-capable service exists in this file.
 */
#include <ACAN2515.h>
#include <SPI.h>

// ---------------------------------------------------------------------------------------
// CHECK THIS FIRST. MCP2515 breakout boards ship with either an 8 MHz or a 16 MHz crystal.
// The wrong value produces a silent bus that looks exactly like bad wiring, and it is the
// single most common cause of a bench that will not come up. Read the can on your board.
// ---------------------------------------------------------------------------------------
static const uint32_t QUARTZ_FREQUENCY = 8UL * 1000UL * 1000UL;  // 8 MHz. Use 16 for a 16 MHz board.

static const uint32_t BUS_BITRATE = 500UL * 1000UL;  // 500 kbps, the diagnostic port rate.

static const byte MCP2515_CS_PIN = 10;
static const byte MCP2515_INT_PIN = 2;

// Bench identifiers, matching firmware/gateway/main/main.c.
static const uint32_t BENCH_REQUEST_ID = 0x7DF;   // Functional request, tester to all ECUs.
static const uint32_t BENCH_RESPONSE_ID = 0x7E8;  // Physical response, first ECU to tester.

static ACAN2515 can(MCP2515_CS_PIN, SPI, MCP2515_INT_PIN);

static uint32_t framesReceived = 0;
static uint32_t framesAnswered = 0;
static uint32_t lastReportMs = 0;

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
    Serial.print(F("MCP2515 ready, listening for 0x"));
    Serial.println(BENCH_REQUEST_ID, HEX);
  } else {
    Serial.print(F("MCP2515 did NOT start, error 0x"));
    Serial.println(errorCode, HEX);
    Serial.println(F("check: SPI wiring, 5V supply, crystal frequency above, INT on D2"));
  }
}

void loop() {
  CANMessage request;
  if (can.receive(request)) {
    framesReceived++;
    printFrame("RX", request);

    if (request.id == BENCH_REQUEST_ID) {
      CANMessage response;
      response.id = BENCH_RESPONSE_ID;
      response.ext = false;
      response.len = 8;
      // Shaped like a Mode 01 positive response for engine speed, padded as the standard
      // does. At F0 it carries no protocol meaning; F1 replaces this with a real decoder.
      response.data[0] = 0x04;
      response.data[1] = 0x41;
      response.data[2] = 0x0C;
      response.data[3] = 0x1A;
      response.data[4] = 0xF8;
      response.data[5] = 0x55;
      response.data[6] = 0x55;
      response.data[7] = 0x55;

      if (can.tryToSend(response)) {
        framesAnswered++;
        printFrame("TX", response);
      } else {
        Serial.println(F("TX queue full, answer dropped"));
      }
    }
  }

  const uint32_t now = millis();
  if (now - lastReportMs >= 5000UL) {
    lastReportMs = now;
    Serial.print(F("received "));
    Serial.print(framesReceived);
    Serial.print(F(", answered "));
    Serial.println(framesAnswered);
  }
}
