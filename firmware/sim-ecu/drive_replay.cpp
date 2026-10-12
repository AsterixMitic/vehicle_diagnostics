/**
 * @file drive_replay.cpp
 * @brief Row lookup in the recorded drive.
 */
#include "drive_replay.h"

#include "drive_profile.h"

namespace obd_sim {

namespace {

/** Index of @p pid in DRIVE_PIDS, or DRIVE_PID_COUNT when the car did not answer it. */
uint8_t index_of(uint8_t pid)
{
    uint8_t i = 0u;
    while (i < DRIVE_PID_COUNT && DRIVE_PIDS[i] != pid) {
        i++;
    }
    return i;
}

}  // namespace

ReplaySource::ReplaySource(uint32_t elapsed_ms)
    : second_((uint16_t)((elapsed_ms / 1000u) % DRIVE_SECONDS))
{
}

bool ReplaySource::supports(uint8_t pid) const
{
    if (pid != 0u && pid % 0x20u == 0u) {
        /* A support PID is declared when the car answered any PID in a later range. */
        for (uint8_t i = 0u; i < DRIVE_PID_COUNT; i++) {
            if (DRIVE_PIDS[i] > pid) {
                return true;
            }
        }
        return false;
    }
    return index_of(pid) < DRIVE_PID_COUNT;
}

uint8_t ReplaySource::encode(uint8_t pid, uint8_t *out) const
{
    const uint8_t index = index_of(pid);
    if (index >= DRIVE_PID_COUNT) {
        return 0u;
    }
    uint16_t offset = (uint16_t)(second_ * DRIVE_ROW_BYTES);
    for (uint8_t i = 0u; i < index; i++) {
        offset = (uint16_t)(offset + DRIVE_WIDTHS[i]);
    }
    for (uint8_t b = 0u; b < DRIVE_WIDTHS[index]; b++) {
        out[b] = DRIVE_READ(&DRIVE_DATA[offset + b]);
    }
    return DRIVE_WIDTHS[index];
}

}  // namespace obd_sim
