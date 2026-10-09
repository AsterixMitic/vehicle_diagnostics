/**
 * @file test_envelope.c
 * @brief Envelope serialization tests, shared by the host build and the target.
 *
 * The expected strings are the exact envelopes the backend's contract tests accept: same field
 * names and order, same number format, same timestamp form.
 */
#include "test_envelope.h"

#include <stdio.h>
#include <string.h>

#include "envelope.h"
#include "obd.h"

static int s_failures;
static int s_checks;

#define CHECK(condition, description)                                            \
    do {                                                                         \
        s_checks++;                                                              \
        if (!(condition)) {                                                      \
            s_failures++;                                                        \
            printf("  FAIL %s:%d  %s\n", __func__, __LINE__, (description));     \
        }                                                                        \
    } while (0)

static const char TRACEPARENT[] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

/* 2026-09-25T10:15:30.125Z */
#define FIXED_US 1790331330125000LL

static hal_can_frame_t frame(uint32_t id, const uint8_t *data)
{
    hal_can_frame_t f;
    memset(&f, 0, sizeof(f));
    f.id = id;
    f.dlc = 8u;
    memcpy(f.data, data, 8u);
    return f;
}

static envelope_header_t header(envelope_mode_t mode)
{
    envelope_header_t h = {"esp32-01", "bench-01", 1042u, FIXED_US, TRACEPARENT, mode};
    return h;
}

/* Three padded answer frames: engine speed 1726.25 rpm, 60 km/h, 50 degC. */
static const uint8_t RPM[8] = {0x04, 0x41, 0x0C, 0x1A, 0xF9, 0xAA, 0xAA, 0xAA};
static const uint8_t SPEED[8] = {0x03, 0x41, 0x0D, 0x3C, 0xAA, 0xAA, 0xAA, 0xAA};
static const uint8_t COOLANT[8] = {0x03, 0x41, 0x05, 0x5A, 0xAA, 0xAA, 0xAA, 0xAA};

static void test_trace_and_time(void)
{
    uint8_t trace[16], span[8];
    for (int i = 0; i < 16; i++) trace[i] = (uint8_t)(0xA0 + i);
    for (int i = 0; i < 8; i++) span[i] = (uint8_t)(0x01 * i);
    char tp[ENVELOPE_TRACEPARENT_LEN];
    envelope_traceparent(tp, trace, span);
    CHECK(strcmp(tp, "00-a0a1a2a3a4a5a6a7a8a9aaabacadaeaf-0001020304050607-01") == 0,
          "traceparent is version 00, lower-case hex, sampled");
    CHECK(strlen(tp) == ENVELOPE_TRACEPARENT_LEN - 1u, "traceparent has the fixed length");

    char ts[ENVELOPE_TIMESTAMP_LEN];
    envelope_timestamp(ts, FIXED_US);
    CHECK(strcmp(ts, "2026-09-25T10:15:30.125+00:00") == 0, "timestamp is ISO 8601 UTC with milliseconds");
    envelope_timestamp(ts, 0);
    CHECK(strcmp(ts, "1970-01-01T00:00:00.000+00:00") == 0, "the epoch formats as the epoch");
}

static void test_p2_readings(void)
{
    hal_can_frame_t frames[3] = {frame(0x7E8u, RPM), frame(0x7E8u, SPEED), frame(0x7E8u, COOLANT)};
    obd_reading_t readings[3];
    for (int i = 0; i < 3; i++) {
        const obd_decode_outcome_t out = obd_decode_frame(&frames[i]);
        CHECK(out.decoded, "the fixed answer frames decode");
        readings[i] = out.reading;
    }
    envelope_header_t h = header(ENVELOPE_MODE_P2);
    char buf[512];
    const int n = envelope_write(buf, sizeof(buf), &h, readings, 3u, NULL, 0u);
    const char *expected =
        "{\"schemaVersion\":1,\"deviceId\":\"esp32-01\",\"vehicleId\":\"bench-01\",\"sequence\":1042,"
        "\"timestampDevice\":\"2026-09-25T10:15:30.125+00:00\","
        "\"traceId\":\"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01\",\"processingMode\":\"P2\","
        "\"payload\":{\"readings\":[{\"name\":\"engine_rpm\",\"value\":1726.25,\"unit\":\"rpm\"},"
        "{\"name\":\"vehicle_speed\",\"value\":60,\"unit\":\"km_h\"},"
        "{\"name\":\"coolant_temp\",\"value\":50,\"unit\":\"degC\"}]}}";
    CHECK(n == (int)strlen(expected) && strcmp(buf, expected) == 0, "P2 envelope matches the contract exactly");
    if (strcmp(buf, expected) != 0) {
        printf("    got      %s\n    expected %s\n", buf, expected);
    }
    CHECK(strcmp(envelope_topic_suffix(ENVELOPE_MODE_P2), "tel/signals") == 0, "P2 goes to tel/signals");
}

static void test_p0_frames(void)
{
    hal_can_frame_t frames[3] = {frame(0x7E8u, RPM), frame(0x7E8u, SPEED), frame(0x7E8u, COOLANT)};
    envelope_header_t h = header(ENVELOPE_MODE_P0);
    char buf[512];
    const int n = envelope_write(buf, sizeof(buf), &h, NULL, 0u, frames, 3u);
    CHECK(n > 0, "P0 envelope fits");
    CHECK(strstr(buf, "\"processingMode\":\"P0\"") != NULL, "P0 envelope names its mode");
    CHECK(strstr(buf, "\"payload\":{\"frames\":[{\"canId\":2024,\"isExtended\":false,\"data\":\"04410C1AF9AAAAAA\"},"
                      "{\"canId\":2024,\"isExtended\":false,\"data\":\"03410D3CAAAAAAAA\"},"
                      "{\"canId\":2024,\"isExtended\":false,\"data\":\"0341055AAAAAAAAA\"}]}}") != NULL,
          "P0 frames keep identifier and all data bytes, padding included");
    CHECK(strcmp(envelope_topic_suffix(ENVELOPE_MODE_P0), "tel/raw") == 0, "P0 goes to tel/raw");
}

static void test_numbers_round_trip(void)
{
    /* 100 * 77 / 255 has no short exact form; it must be written with enough digits. */
    obd_reading_t r = {0x04u, "engine_load", "percent", 100.0 * 77.0 / 255.0};
    envelope_header_t h = header(ENVELOPE_MODE_P2);
    char buf[512];
    CHECK(envelope_write(buf, sizeof(buf), &h, &r, 1u, NULL, 0u) > 0, "fits");
    CHECK(strstr(buf, "\"value\":30.19607843137255,") != NULL, "a value reads back as the same double");
}

static void test_overflow_is_reported(void)
{
    hal_can_frame_t frames[3] = {frame(0x7E8u, RPM), frame(0x7E8u, SPEED), frame(0x7E8u, COOLANT)};
    envelope_header_t h = header(ENVELOPE_MODE_P0);
    char buf[64];
    CHECK(envelope_write(buf, sizeof(buf), &h, NULL, 0u, frames, 3u) == -1, "a buffer too small is an error");
}

int envelope_tests_run(void)
{
    printf("envelope\n");
    test_trace_and_time();
    test_p2_readings();
    test_p0_frames();
    test_numbers_round_trip();
    test_overflow_is_reported();
    printf("%d checks, %d failures\n", s_checks, s_failures);
    return s_failures;
}
