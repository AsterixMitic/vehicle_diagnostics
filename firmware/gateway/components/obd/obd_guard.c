/**
 * @file obd_guard.c
 * @brief The vehicle error guard rule.
 */
#include "obd_guard.h"

bool obd_guard_should_stop(obd_target_t target, uint32_t bus_errors_since_start, uint32_t threshold,
                           bool bus_off)
{
    if (target != OBD_TARGET_VEHICLE) {
        return false;
    }
    return bus_off || bus_errors_since_start > threshold;
}
