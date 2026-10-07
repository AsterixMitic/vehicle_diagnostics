/**
 * @file obd.h
 * @brief OBD-II Mode 01: the parameter table, the frame decoder and the support bitmaps.
 *
 * Mirrors the backend's ObdMode01Decoder exactly: the same 14 PIDs, signal names, units,
 * minimum data lengths and SAE J1979 formulas, and the same reasons for a frame that yields
 * no reading. Both decoders are tested against the same capture files, so a P0 message
 * decoded on the backend and a P2 message decoded here carry identical values.
 *
 * Decoding works on raw frames, not on ISO-TP messages, because the backend classifies frames
 * the same way: a first or consecutive frame is unsupported, not reassembled.
 *
 * No I/O, no allocation, no clock. Testable on a development machine.
 */
#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "hal_can.h"

#ifdef __cplusplus
extern "C" {
#endif

/** Number of Mode 01 parameters the system decodes. */
#define OBD_PID_COUNT 14u

/** Mode 01 request and positive response service identifiers. */
#define OBD_SERVICE_CURRENT_DATA 0x01u
#define OBD_POSITIVE_CURRENT_DATA 0x41u
#define OBD_NEGATIVE_RESPONSE 0x7Fu

/** Why a frame produced no reading. A closed set: every value is a counter tag. */
typedef enum {
    OBD_IGNORE_NONE = 0,
    OBD_IGNORE_NOT_OBD_RESPONSE,
    OBD_IGNORE_NOT_SINGLE_FRAME,
    OBD_IGNORE_MULTI_FRAME_UNSUPPORTED,
    OBD_IGNORE_NEGATIVE_RESPONSE,
    OBD_IGNORE_UNSUPPORTED_SERVICE,
    OBD_IGNORE_UNSUPPORTED_PID,
    OBD_IGNORE_SHORT_PAYLOAD,
    OBD_IGNORE_REASON_COUNT,
} obd_ignore_reason_t;

/** Stable snake_case tag, identical to the backend's, e.g. "negative_response". */
const char *obd_ignore_tag(obd_ignore_reason_t reason);

/** One row of the Mode 01 table. */
typedef struct {
    uint8_t pid;
    const char *name;   /**< Signal name as the gateway emits it in mode P2, e.g. "engine_rpm". */
    const char *unit;   /**< ASCII unit, e.g. "km_h". */
    uint8_t min_bytes;  /**< Data bytes that must follow the PID. */
} obd_pid_info_t;

/** The table, in PID order. @c OBD_PID_COUNT entries. */
const obd_pid_info_t *obd_pid_table(void);

/** The row for @p pid, or NULL when the system does not decode it. */
const obd_pid_info_t *obd_pid_info(uint8_t pid);

/**
 * True for OBD-II diagnostic response identifiers: 11-bit 0x7E8 to 0x7EF, or 29-bit
 * 0x18DAF1xx (ISO 15765-4 physical addressing, target 0xF1 = external tester).
 */
bool obd_is_response_id(uint32_t id, bool extended);

/** A physical value decoded from one frame. */
typedef struct {
    uint8_t pid;
    const char *name;  /**< Points into the table. */
    const char *unit;  /**< Points into the table. */
    double value;
} obd_reading_t;

/** What decoding one frame produced: a reading, or the reason there is none. */
typedef struct {
    bool decoded;
    obd_reading_t reading;      /**< Valid when @c decoded. */
    obd_ignore_reason_t reason; /**< OBD_IGNORE_NONE when @c decoded. */
} obd_decode_outcome_t;

/** Decode one Mode 01 positive response carried in a single frame. */
obd_decode_outcome_t obd_decode_frame(const hal_can_frame_t *frame);

/**
 * Which PIDs an ECU declared supported, from the bitmaps of PIDs 0x00, 0x20 and 0x40.
 * Range r covers PIDs r+1 to r+0x20; the last bit of each range declares the next range.
 */
typedef struct {
    uint32_t bitmap[3];
    bool known[3];
} obd_support_t;

void obd_support_init(obd_support_t *support);

/** True for 0x00, 0x20 and 0x40, the PIDs whose answer is a support bitmap. */
bool obd_is_support_pid(uint8_t pid);

/**
 * Record the bitmap an ECU returned for @p range_pid (four data bytes, A first).
 * @return false when @p range_pid is not a support PID.
 */
bool obd_support_apply(obd_support_t *support, uint8_t range_pid, const uint8_t data[4]);

/** True when @p pid was declared supported in a bitmap that has been applied. */
bool obd_support_has(const obd_support_t *support, uint8_t pid);

/** True when the bitmap for @p range_pid was applied and declares the following range. */
bool obd_support_declares_next(const obd_support_t *support, uint8_t range_pid);

/**
 * Recognise a support-bitmap answer: a single frame on a response identifier carrying
 * 0x41, a support PID and four data bytes.
 * @return true and fills @p range_pid and @p data when it is one.
 */
bool obd_parse_support_response(const hal_can_frame_t *frame, uint8_t *range_pid, uint8_t data[4]);

#ifdef __cplusplus
}
#endif
