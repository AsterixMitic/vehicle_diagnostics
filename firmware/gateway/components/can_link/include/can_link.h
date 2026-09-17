/**
 * @file can_link.h
 * @brief Link layer: owns the receive task, the frame buffer and bus health.
 *
 * The receive task is the highest-priority task in the firmware and does nothing but move
 * frames from the driver into the ring buffer. Consumers pull from the buffer at their own
 * pace; a slow consumer causes counted drops, never a stall on the bus side
 * (architecture.md §7, §12.1).
 */
#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "hal_can.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    int tx_gpio;
    int rx_gpio;
    uint32_t bitrate_kbps;
    hal_can_filter_mode_t filter_mode;
    uint32_t filter_id;
    uint32_t filter_mask;
    /** Receive task priority. Must exceed every consumer's priority. */
    unsigned task_priority;
} can_link_config_t;

/** A default bench configuration: 500 kbps, accept every identifier. */
can_link_config_t can_link_default_config(void);

typedef struct {
    uint32_t rx_frames;   /**< Frames taken off the bus. */
    uint32_t tx_frames;   /**< Frames put on the bus. */
    uint32_t buffered;    /**< Frames waiting in the buffer right now. */
    uint32_t dropped;     /**< Frames lost because the consumer fell behind. */
    uint32_t bus_errors;  /**< Controller-reported bus errors. */
    uint32_t rx_missed;   /**< Frames lost inside the driver queue. */
    uint32_t recoveries;  /**< Times the controller was recovered from bus-off. */
    hal_can_state_t state;
} can_link_stats_t;

typedef enum {
    CAN_LINK_OK = 0,
    CAN_LINK_ERR_INVALID_ARG,
    CAN_LINK_ERR_INVALID_STATE,
    CAN_LINK_ERR_TIMEOUT,
    CAN_LINK_ERR_FAILED,
} can_link_err_t;

/** Configure the controller, start it, and launch the receive task. */
can_link_err_t can_link_start(const can_link_config_t *config);

/** Stop the receive task and release the controller. */
can_link_err_t can_link_stop(void);

/**
 * Take the oldest buffered frame.
 * @return true when a frame was produced; false when nothing is buffered.
 */
bool can_link_receive(hal_can_frame_t *out_frame);

/** Put one frame on the bus. */
can_link_err_t can_link_transmit(const hal_can_frame_t *frame, uint32_t timeout_ms);

/** Snapshot of everything the evaluation needs to know about the bus. */
void can_link_get_stats(can_link_stats_t *out_stats);

#ifdef __cplusplus
}
#endif
