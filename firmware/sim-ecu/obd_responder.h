/**
 * @file obd_responder.h
 * @brief The simulated engine ECU's OBD-II Mode 01 behaviour, without Arduino dependencies.
 *
 * Answers Mode 01 requests on the functional (0x7DF) and the physical (0x7E0) identifier
 * with a single frame on 0x7E8, encoded with the SAE J1979 scaling and padded with 0x55.
 * Declares exactly what it answers in the support bitmaps of PIDs 0x00, 0x20 and 0x40, and
 * stays silent for anything it does not support, as a real ECU does on a functional request.
 *
 * SAFETY: a bench simulator. It implements read-only Mode 01 and nothing else.
 */
#pragma once

#include <stdint.h>

#include "vehicle_model.h"

namespace obd_sim {

const uint32_t FUNCTIONAL_REQUEST_ID = 0x7DFu;
const uint32_t PHYSICAL_REQUEST_ID = 0x7E0u;
const uint32_t RESPONSE_ID = 0x7E8u;

struct Frame {
    uint32_t id;
    uint8_t dlc;
    uint8_t data[8];
};

/** True for the PIDs the simulator answers, support PIDs 0x20 and 0x40 included. */
bool supports(uint8_t pid);

/**
 * The value the model holds for @p pid, in the unit the PID is encoded in, clamped to the
 * range the PID can carry. False for PIDs that are not a measured value.
 */
bool model_value(const VehicleState &state, uint8_t pid, float *value);

/**
 * The answer to one received frame.
 * @return true and fills @p response when the frame is a Mode 01 request this ECU answers.
 */
bool respond(const VehicleState &state, uint32_t id, const uint8_t *data, uint8_t dlc, Frame *response);

}  // namespace obd_sim
