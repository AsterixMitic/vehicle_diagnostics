/**
 * @file drive_replay.h
 * @brief Answers from a recorded drive: the bytes the real engine ECU sent, second by second.
 *
 * The drive (drive_profile.h) loops: second s of the replay is row s mod DRIVE_SECONDS. Only
 * the PIDs the car answered are supported, and the support PIDs 0x20, 0x40, ... are declared
 * exactly when the car answered a PID above them, so the gateway discovers what it found on
 * the car.
 *
 * Plain C++ with no Arduino dependency apart from reading flash, so it tests on a host.
 */
#pragma once

#include <stdint.h>

#include "obd_responder.h"

namespace obd_sim {

class ReplaySource : public Source {
public:
    /** The drive as it stands @p elapsed_ms after the replay started. */
    explicit ReplaySource(uint32_t elapsed_ms);

    bool supports(uint8_t pid) const;
    uint8_t encode(uint8_t pid, uint8_t *out) const;

    /** The row being replayed, 0 to DRIVE_SECONDS - 1. */
    uint16_t second() const { return second_; }

private:
    uint16_t second_;
};

}  // namespace obd_sim
