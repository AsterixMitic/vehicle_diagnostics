/**
 * @file hal_can.h
 * @brief Vendor-free CAN interface. The only component allowed to talk to the controller.
 *
 * This header contains no ESP-IDF include on purpose: everything above the hardware
 * abstraction must compile on a development machine with no framework installed, which is
 * what makes the protocol layers testable without an ESP32 (architecture.md §7).
 */
#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/** Classic CAN 2.0 carries at most eight data bytes. CAN FD is out of scope. */
#define HAL_CAN_MAX_DLC 8u

/** Largest 11-bit and 29-bit identifiers. */
#define HAL_CAN_MAX_STD_ID 0x7FFu
#define HAL_CAN_MAX_EXT_ID 0x1FFFFFFFu

/** One classic CAN frame, timestamped as early as the driver allows. */
typedef struct {
    uint32_t id;                       /**< 11-bit or 29-bit identifier. */
    uint8_t data[HAL_CAN_MAX_DLC];     /**< Payload; only the first @c dlc bytes are valid. */
    uint8_t dlc;                       /**< Data length, 0 to HAL_CAN_MAX_DLC. */
    bool extended;                     /**< True when @c id is 29-bit. */
    /**
     * Receive time in microseconds since boot, taken the moment the frame left the driver
     * queue. Sampling jitter is a measured quantity for the evaluation, so this must never
     * be filled in later by a consumer.
     */
    uint64_t timestamp_us;
} hal_can_frame_t;

/** Which identifiers reach the consumer. */
typedef enum {
    HAL_CAN_FILTER_ACCEPT_ALL = 0, /**< Everything on the bus. Required for recording captures. */
    HAL_CAN_FILTER_MASK,           /**< Accept where (id & mask) == (filter_id & mask). */
} hal_can_filter_mode_t;

typedef struct {
    int tx_gpio;                        /**< Controller transmit pin, GPIO5 on this board. */
    int rx_gpio;                        /**< Controller receive pin, GPIO4 on this board. */
    uint32_t bitrate_kbps;              /**< 500 for the diagnostic port. */
    hal_can_filter_mode_t filter_mode;
    uint32_t filter_id;                 /**< Used only with HAL_CAN_FILTER_MASK. */
    uint32_t filter_mask;               /**< Used only with HAL_CAN_FILTER_MASK. */
} hal_can_config_t;

/** Controller state, mirrored from the peripheral. */
typedef enum {
    HAL_CAN_STATE_STOPPED = 0,
    HAL_CAN_STATE_RUNNING,
    HAL_CAN_STATE_BUS_OFF,     /**< Too many errors; needs recovery before traffic resumes. */
    HAL_CAN_STATE_RECOVERING,
} hal_can_state_t;

/** Everything that can be lost, counted. Reported by the evaluation, not just for debugging. */
typedef struct {
    uint32_t rx_frames;   /**< Frames handed to the caller. */
    uint32_t tx_frames;   /**< Frames accepted by the controller for transmission. */
    uint32_t bus_errors;  /**< Controller-reported bus errors. */
    uint32_t rx_missed;   /**< Frames the driver queue lost before we read them. */
} hal_can_counters_t;

/** Expected failures are return values, never exceptions or asserts. */
typedef enum {
    HAL_CAN_OK = 0,
    HAL_CAN_ERR_INVALID_ARG,
    HAL_CAN_ERR_INVALID_STATE,
    HAL_CAN_ERR_TIMEOUT,       /**< No frame within the timeout; an ordinary idle bus. */
    HAL_CAN_ERR_FAILED,
} hal_can_err_t;

/** Install and configure the controller. Does not start traffic. */
hal_can_err_t hal_can_init(const hal_can_config_t *config);

/** Begin receiving and transmitting. */
hal_can_err_t hal_can_start(void);

/** Stop traffic and uninstall the driver. */
hal_can_err_t hal_can_stop(void);

/**
 * Wait for one frame.
 * @return HAL_CAN_OK on success, HAL_CAN_ERR_TIMEOUT when the bus was idle.
 */
hal_can_err_t hal_can_receive(hal_can_frame_t *out_frame, uint32_t timeout_ms);

/** Queue one frame for transmission. */
hal_can_err_t hal_can_transmit(const hal_can_frame_t *frame, uint32_t timeout_ms);

hal_can_err_t hal_can_get_counters(hal_can_counters_t *out_counters);

hal_can_state_t hal_can_get_state(void);

/** Start recovery from the bus-off state. Returns to running once the bus is healthy. */
hal_can_err_t hal_can_recover(void);

/** Microseconds since boot, for callers that need the same clock as @c timestamp_us. */
uint64_t hal_can_now_us(void);

#ifdef __cplusplus
}
#endif
