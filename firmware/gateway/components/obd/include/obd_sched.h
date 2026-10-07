/**
 * @file obd_sched.h
 * @brief Mode 01 polling scheduler: discovery of supported PIDs, then one request per PID
 *        per cycle, with a bounded wait for each answer.
 *
 * An explicit state machine with no I/O, like ISO-TP: it never reads a clock and never
 * touches the bus. The caller passes the current time and every received frame, and the
 * outcome says what to transmit and what happened. That is what lets the whole schedule,
 * timeouts included, be tested on a development machine to the microsecond.
 *
 * Every frame the scheduler asks the caller to transmit is a Mode 01 (read current data)
 * request. There is no other request in this component.
 */
#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "hal_can.h"
#include "obd.h"

#ifdef __cplusplus
extern "C" {
#endif

/** Physical request to the engine ECU, its answer, and the functional request to all ECUs. */
#define OBD_ENGINE_REQUEST_ID 0x7E0u
#define OBD_ENGINE_RESPONSE_ID 0x7E8u
#define OBD_FUNCTIONAL_REQUEST_ID 0x7DFu

/** ISO 15765-4 P2 for CAN: an ECU answers within 50 ms. */
#define OBD_RESPONSE_TIMEOUT_US 50000u

typedef enum {
    OBD_SCHED_DISCOVER_SEND = 0, /**< A support bitmap request is due. */
    OBD_SCHED_DISCOVER_WAIT,     /**< Waiting for a support bitmap. */
    OBD_SCHED_CYCLE_WAIT,        /**< Polling: waiting for the next cycle to start. */
    OBD_SCHED_POLL_WAIT,         /**< Polling: waiting for the answer to one PID. */
    OBD_SCHED_DISCOVERY_FAILED,  /**< The ECU declared nothing; nothing is ever requested. */
    OBD_SCHED_STOPPED,           /**< Stopped by the caller; nothing is ever requested. */
} obd_sched_state_t;

typedef struct {
    uint32_t request_id;         /**< OBD_ENGINE_REQUEST_ID, or OBD_FUNCTIONAL_REQUEST_ID. */
    uint32_t response_id;        /**< Answers are taken only from here; OBD_ENGINE_RESPONSE_ID. */
    uint64_t period_us;          /**< From the start of one cycle to the start of the next. */
    uint64_t timeout_us;         /**< Wait for each answer; OBD_RESPONSE_TIMEOUT_US. */
    uint8_t discovery_attempts;  /**< Requests per support bitmap before giving up on it. */
} obd_sched_config_t;

/** What a call produced besides, possibly, a frame to transmit. */
typedef enum {
    OBD_EVENT_NONE = 0,
    OBD_EVENT_READING,           /**< @c reading holds a decoded answer. */
    OBD_EVENT_TIMEOUT,           /**< @c pid was not answered in time. */
    OBD_EVENT_NEGATIVE_RESPONSE, /**< @c pid was answered negatively. */
    OBD_EVENT_DISCOVERED,        /**< Discovery finished; polling starts. */
    OBD_EVENT_DISCOVERY_FAILED,  /**< No bitmap was answered, or none of the PIDs is supported. */
    OBD_EVENT_IGNORED,           /**< A frame that is not a Mode 01 answer; @c reason says why. */
    OBD_EVENT_OTHER_ECU,         /**< A Mode 01 answer from an ECU other than the engine ECU. */
    OBD_EVENT_UNEXPECTED,        /**< An answer from the engine ECU that was not awaited. */
} obd_event_t;

typedef struct {
    bool send_frame;             /**< Transmit @c frame now. */
    hal_can_frame_t frame;
    obd_event_t event;
    uint8_t pid;                 /**< The PID the event concerns. */
    obd_reading_t reading;       /**< Valid for OBD_EVENT_READING. */
    uint32_t source_id;          /**< Identifier of the frame behind the event, if any. */
    obd_ignore_reason_t reason;  /**< Valid for OBD_EVENT_IGNORED. */
} obd_sched_outcome_t;

typedef struct {
    uint32_t requests;
    uint32_t answers;
    uint32_t timeouts;
    uint32_t negative;
    uint32_t other_ecu;
    uint32_t unexpected;
    uint32_t ignored[OBD_IGNORE_REASON_COUNT];
    uint32_t cycles;
} obd_sched_counters_t;

typedef struct {
    obd_sched_config_t config;
    obd_sched_state_t state;
    obd_support_t support;
    uint8_t range_pid;            /**< Support PID being discovered. */
    uint8_t attempts;             /**< Requests sent for @c range_pid. */
    uint8_t poll_pids[OBD_PID_COUNT];
    uint8_t poll_count;
    uint8_t poll_index;           /**< PID awaited in the current cycle. */
    uint64_t deadline_us;         /**< End of the current wait. */
    uint64_t cycle_start_us;
    obd_sched_counters_t counters;
} obd_sched_t;

/** Defaults: physical addressing to the engine ECU, 1 s period, 50 ms timeout, 3 attempts. */
obd_sched_config_t obd_sched_default_config(void);

void obd_sched_init(obd_sched_t *sched, const obd_sched_config_t *config);

/** Advance time: sends what is due, reports timeouts. */
void obd_sched_on_tick(obd_sched_t *sched, uint64_t now_us, obd_sched_outcome_t *out);

/** Feed one received frame. */
void obd_sched_on_frame(obd_sched_t *sched, const hal_can_frame_t *frame, uint64_t now_us,
                        obd_sched_outcome_t *out);

/** Stop for good: no further request is ever produced. */
void obd_sched_stop(obd_sched_t *sched);

/** True once discovery finished and @p pid is among the PIDs being polled. */
bool obd_sched_polls(const obd_sched_t *sched, uint8_t pid);

#ifdef __cplusplus
}
#endif
