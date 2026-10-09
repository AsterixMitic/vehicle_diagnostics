/**
 * @file envelope.h
 * @brief The version-1 JSON envelope of message-contracts, built from one poll cycle.
 *
 * Mode P2 carries the readings the gateway decoded (topic suffix tel/signals); mode P0 carries
 * the answer frames exactly as they were received (tel/raw), for the backend to decode. Both use
 * the same envelope, so the only difference between the modes is where decoding happens.
 *
 * Nothing here reads a clock, a random source or the network: the caller passes the time, the
 * trace identifiers and the buffer. Output that does not fit is reported, never truncated.
 */
#pragma once

#include <stddef.h>
#include <stdint.h>

#include "hal_can.h"
#include "obd.h"

#ifdef __cplusplus
extern "C" {
#endif

#define ENVELOPE_SCHEMA_VERSION 1
/** "00-" + 32 hex + "-" + 16 hex + "-01" and the terminator. */
#define ENVELOPE_TRACEPARENT_LEN 56u
/** "2026-10-08T07:15:30.125+00:00" and the terminator. */
#define ENVELOPE_TIMESTAMP_LEN 30u

typedef enum {
    ENVELOPE_MODE_P0 = 0,  /**< Raw answer frames; the backend decodes. */
    ENVELOPE_MODE_P2,      /**< Readings decoded on the gateway. */
} envelope_mode_t;

typedef struct {
    const char *device_id;
    const char *vehicle_id;
    uint64_t sequence;
    int64_t timestamp_unix_us;       /**< Wall-clock time of the cycle's first answer, UTC. */
    const char *traceparent;         /**< From envelope_traceparent(). */
    envelope_mode_t mode;
} envelope_header_t;

/** "P0" or "P2". */
const char *envelope_mode_name(envelope_mode_t mode);

/** Topic suffix after veh/{v}/dev/{d}/: "tel/raw" or "tel/signals". */
const char *envelope_topic_suffix(envelope_mode_t mode);

/** W3C traceparent "00-<trace id>-<span id>-01" in lower-case hex. */
void envelope_traceparent(char out[ENVELOPE_TRACEPARENT_LEN], const uint8_t trace_id[16], const uint8_t span_id[8]);

/** ISO 8601 UTC with milliseconds, e.g. "2026-10-08T07:15:30.125+00:00". */
void envelope_timestamp(char out[ENVELOPE_TIMESTAMP_LEN], int64_t unix_us);

/**
 * Write the envelope for one cycle into @p buf.
 * In mode P2 @p readings is used and @p frames ignored; in mode P0 the reverse.
 * @return the length written, without the terminator, or -1 when @p cap is too small.
 */
int envelope_write(char *buf, size_t cap, const envelope_header_t *header,
                   const obd_reading_t *readings, size_t reading_count,
                   const hal_can_frame_t *frames, size_t frame_count);

#ifdef __cplusplus
}
#endif
