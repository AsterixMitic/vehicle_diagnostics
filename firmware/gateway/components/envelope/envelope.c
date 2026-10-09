/**
 * @file envelope.c
 * @brief JSON envelope serialization, see envelope.h.
 */
/* gmtime_r is POSIX; the host build is strict C11 otherwise. */
#define _POSIX_C_SOURCE 200809L

#include "envelope.h"

#include <inttypes.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct {
    char *buf;
    size_t cap;
    size_t len;
    bool overflow;
} writer_t;

static void put(writer_t *w, const char *fmt, ...)
{
    if (w->overflow) {
        return;
    }
    va_list args;
    va_start(args, fmt);
    const int n = vsnprintf(w->buf + w->len, w->cap - w->len, fmt, args);
    va_end(args);
    if (n < 0 || (size_t)n >= w->cap - w->len) {
        w->overflow = true;
        return;
    }
    w->len += (size_t)n;
}

/* The shortest decimal that reads back as the same double, as the backend's serializer writes
 * it: values decoded on the gateway and on the backend must compare equal, not merely close. */
static void put_number(writer_t *w, double value)
{
    char text[32];
    if (value == (double)(int64_t)value && value > -1e15 && value < 1e15) {
        put(w, "%" PRId64, (int64_t)value);  /* %g would write 60 as 6e+01 */
        return;
    }
    for (int precision = 1; precision <= 17; precision++) {
        snprintf(text, sizeof(text), "%.*g", precision, value);
        if (strtod(text, NULL) == value) {
            break;
        }
    }
    put(w, "%s", text);
}

const char *envelope_mode_name(envelope_mode_t mode)
{
    return mode == ENVELOPE_MODE_P0 ? "P0" : "P2";
}

const char *envelope_topic_suffix(envelope_mode_t mode)
{
    return mode == ENVELOPE_MODE_P0 ? "tel/raw" : "tel/signals";
}

void envelope_traceparent(char out[ENVELOPE_TRACEPARENT_LEN], const uint8_t trace_id[16], const uint8_t span_id[8])
{
    static const char HEX[] = "0123456789abcdef";
    size_t i = 0;
    out[i++] = '0';
    out[i++] = '0';
    out[i++] = '-';
    for (size_t k = 0; k < 16u; k++) {
        out[i++] = HEX[trace_id[k] >> 4];
        out[i++] = HEX[trace_id[k] & 0x0Fu];
    }
    out[i++] = '-';
    for (size_t k = 0; k < 8u; k++) {
        out[i++] = HEX[span_id[k] >> 4];
        out[i++] = HEX[span_id[k] & 0x0Fu];
    }
    out[i++] = '-';
    out[i++] = '0';
    out[i++] = '1';  /* sampled */
    out[i] = '\0';
}

void envelope_timestamp(char out[ENVELOPE_TIMESTAMP_LEN], int64_t unix_us)
{
    const time_t seconds = (time_t)(unix_us / 1000000);
    const int millis = (int)((unix_us % 1000000) / 1000);
    struct tm utc;
    gmtime_r(&seconds, &utc);
    /* The fields of a valid struct tm always fit; the wide buffer only keeps the compiler's
     * truncation analysis, which assumes any int, from failing the build. */
    char wide[96];
    snprintf(wide, sizeof(wide), "%04d-%02d-%02dT%02d:%02d:%02d.%03d+00:00", utc.tm_year + 1900, utc.tm_mon + 1,
             utc.tm_mday, utc.tm_hour, utc.tm_min, utc.tm_sec, millis);
    memcpy(out, wide, ENVELOPE_TIMESTAMP_LEN - 1u);
    out[ENVELOPE_TIMESTAMP_LEN - 1u] = '\0';
}

int envelope_write(char *buf, size_t cap, const envelope_header_t *header,
                   const obd_reading_t *readings, size_t reading_count,
                   const hal_can_frame_t *frames, size_t frame_count)
{
    if (buf == NULL || cap == 0u || header == NULL) {
        return -1;
    }
    writer_t w = {buf, cap, 0u, false};
    char timestamp[ENVELOPE_TIMESTAMP_LEN];
    envelope_timestamp(timestamp, header->timestamp_unix_us);

    put(&w,
        "{\"schemaVersion\":%d,\"deviceId\":\"%s\",\"vehicleId\":\"%s\",\"sequence\":%" PRIu64
        ",\"timestampDevice\":\"%s\",\"traceId\":\"%s\",\"processingMode\":\"%s\",\"payload\":",
        ENVELOPE_SCHEMA_VERSION, header->device_id, header->vehicle_id, header->sequence, timestamp,
        header->traceparent, envelope_mode_name(header->mode));

    if (header->mode == ENVELOPE_MODE_P2) {
        put(&w, "{\"readings\":[");
        for (size_t i = 0; i < reading_count; i++) {
            put(&w, "%s{\"name\":\"%s\",\"value\":", i == 0u ? "" : ",", readings[i].name);
            put_number(&w, readings[i].value);
            put(&w, ",\"unit\":\"%s\"}", readings[i].unit);
        }
    } else {
        put(&w, "{\"frames\":[");
        for (size_t i = 0; i < frame_count; i++) {
            put(&w, "%s{\"canId\":%" PRIu32 ",\"isExtended\":%s,\"data\":\"", i == 0u ? "" : ",", frames[i].id,
                frames[i].extended ? "true" : "false");
            for (uint8_t k = 0; k < frames[i].dlc && k < 8u; k++) {
                put(&w, "%02X", frames[i].data[k]);
            }
            put(&w, "\"}");
        }
    }
    put(&w, "]}}");
    return w.overflow ? -1 : (int)w.len;
}
