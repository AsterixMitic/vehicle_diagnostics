/**
 * @file obd_guard.h
 * @brief When to stop transmitting on a vehicle bus.
 *
 * On a real vehicle the gateway is a guest: if the bus shows errors after it starts
 * requesting, or the controller goes bus-off, the safe reaction is to stop sending and only
 * listen until someone resets the device. On the bench the same faults are expected while
 * wiring and are recovered automatically. A pure rule, so it is tested off target.
 */
#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    OBD_TARGET_BENCH = 0,  /**< The simulator on the desk. */
    OBD_TARGET_VEHICLE,    /**< A real vehicle. */
} obd_target_t;

/**
 * True when requests must stop: target vehicle, and either the controller is bus-off or the
 * bus errors counted since start exceed @p threshold. Always false on the bench.
 */
bool obd_guard_should_stop(obd_target_t target, uint32_t bus_errors_since_start, uint32_t threshold,
                           bool bus_off);

#ifdef __cplusplus
}
#endif
