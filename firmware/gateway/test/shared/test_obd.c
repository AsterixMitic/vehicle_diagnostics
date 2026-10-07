/**
 * @file test_obd.c
 * @brief OBD decoder and support-bitmap tests, shared by the host build and the target.
 *
 * The decoder test replays the backend's capture fixture
 * (experiments/captures/synthetic-obd-mode01.log) and checks every frame against the same
 * expectation file the backend's decoder test reads. Both decoders passing it is what makes
 * P0 decoded on the backend and P2 decoded on the gateway carry identical values.
 */
#include "test_obd.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

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

#define MAX_ENTRIES 64

/* ---------------------------------------------------------------------------------------- */
/* Fixture parsing: the candump log and the expectation file, each in its fixed shape.       */
/* ---------------------------------------------------------------------------------------- */

typedef struct {
    int line;
    hal_can_frame_t frame;
} capture_entry_t;

typedef struct {
    int line;
    bool decoded;
    char name[32];
    char unit[16];
    char reason[32];
    double value;
} expectation_t;

static int hex_digit(char c)
{
    if (c >= '0' && c <= '9') {
        return c - '0';
    }
    if (c >= 'a' && c <= 'f') {
        return c - 'a' + 10;
    }
    if (c >= 'A' && c <= 'F') {
        return c - 'A' + 10;
    }
    return -1;
}

/** One candump line, "(seconds.micros) interface ID#HEX"; three id digits = 11-bit, eight = 29-bit. */
static bool parse_candump_line(const char *line, size_t length, hal_can_frame_t *out)
{
    const char *end = line + length;
    const char *p = memchr(line, ')', length);
    if (p == NULL) {
        return false;
    }
    p++;
    while (p < end && *p == ' ') {
        p++;
    }
    while (p < end && *p != ' ') {
        p++;  /* interface name */
    }
    while (p < end && *p == ' ') {
        p++;
    }

    uint32_t id = 0u;
    int digits = 0;
    while (p < end && *p != '#') {
        const int d = hex_digit(*p);
        if (d < 0) {
            return false;
        }
        id = (id << 4) | (uint32_t)d;
        digits++;
        p++;
    }
    if (p >= end || (digits != 3 && digits != 8)) {
        return false;
    }
    p++;  /* '#' */

    memset(out, 0, sizeof(*out));
    out->id = id;
    out->extended = digits == 8;
    while (p + 1 < end && hex_digit(p[0]) >= 0 && hex_digit(p[1]) >= 0 && out->dlc < HAL_CAN_MAX_DLC) {
        out->data[out->dlc] = (uint8_t)((hex_digit(p[0]) << 4) | hex_digit(p[1]));
        out->dlc++;
        p += 2;
    }
    return true;
}

static int parse_capture(const char *text, capture_entry_t *entries, int capacity)
{
    int count = 0;
    int line_number = 0;
    const char *p = text;
    while (*p != '\0' && count < capacity) {
        const char *eol = strchr(p, '\n');
        const size_t length = eol != NULL ? (size_t)(eol - p) : strlen(p);
        line_number++;
        if (length > 0u && p[0] != '#' && p[0] != '\r') {
            if (parse_candump_line(p, length, &entries[count].frame)) {
                entries[count].line = line_number;
                count++;
            }
        }
        if (eol == NULL) {
            break;
        }
        p = eol + 1;
    }
    return count;
}

/** Copy the string value of "key" from the object [begin, end) into out. */
static bool string_field(const char *begin, const char *end, const char *key, char *out, size_t size)
{
    char pattern[24];
    (void)snprintf(pattern, sizeof(pattern), "\"%s\"", key);
    const char *at = strstr(begin, pattern);
    if (at == NULL || at >= end) {
        return false;
    }
    const char *open = strchr(at + strlen(pattern), '"');
    if (open == NULL || open >= end) {
        return false;
    }
    const char *close = strchr(open + 1, '"');
    if (close == NULL || close >= end || (size_t)(close - open - 1) >= size) {
        return false;
    }
    memcpy(out, open + 1, (size_t)(close - open - 1));
    out[close - open - 1] = '\0';
    return true;
}

/** Parse the numeric value of "key" from the object [begin, end). */
static bool number_field(const char *begin, const char *end, const char *key, double *out)
{
    char pattern[24];
    (void)snprintf(pattern, sizeof(pattern), "\"%s\"", key);
    const char *at = strstr(begin, pattern);
    if (at == NULL || at >= end) {
        return false;
    }
    const char *colon = strchr(at + strlen(pattern), ':');
    if (colon == NULL || colon >= end) {
        return false;
    }
    char *stop = NULL;
    *out = strtod(colon + 1, &stop);
    return stop != colon + 1;
}

/** The expectation file is an array of flat objects; this reads exactly that shape. */
static int parse_expectations(const char *text, expectation_t *out, int capacity)
{
    int count = 0;
    const char *p = text;
    while (count < capacity) {
        const char *begin = strchr(p, '{');
        if (begin == NULL) {
            break;
        }
        const char *end = strchr(begin, '}');
        if (end == NULL) {
            break;
        }

        expectation_t *e = &out[count];
        memset(e, 0, sizeof(*e));
        double line = 0.0;
        char outcome[16] = {0};
        if (!number_field(begin, end, "line", &line) ||
            !string_field(begin, end, "outcome", outcome, sizeof(outcome))) {
            break;
        }
        e->line = (int)line;
        e->decoded = strcmp(outcome, "decoded") == 0;
        if (e->decoded) {
            (void)string_field(begin, end, "name", e->name, sizeof(e->name));
            (void)string_field(begin, end, "unit", e->unit, sizeof(e->unit));
            (void)number_field(begin, end, "value", &e->value);
        } else {
            (void)string_field(begin, end, "reason", e->reason, sizeof(e->reason));
        }
        count++;
        p = end + 1;
    }
    return count;
}

/* ---------------------------------------------------------------------------------------- */
/* Decoder                                                                                   */
/* ---------------------------------------------------------------------------------------- */

static capture_entry_t s_entries[MAX_ENTRIES];
static expectation_t s_expected[MAX_ENTRIES];

static void test_fixture_decodes_to_its_expectation(void)
{
    const char *log = obd_fixture_log();
    const char *expected = obd_fixture_expected();
    CHECK(log != NULL && expected != NULL, "the capture fixture and its expectation are loaded");
    if (log == NULL || expected == NULL) {
        return;
    }

    const int entries = parse_capture(log, s_entries, MAX_ENTRIES);
    const int expectations = parse_expectations(expected, s_expected, MAX_ENTRIES);
    CHECK(entries > 0, "the capture holds frames");
    CHECK(entries == expectations, "every captured frame has an expectation");

    const int n = entries < expectations ? entries : expectations;
    for (int i = 0; i < n; i++) {
        const expectation_t *e = &s_expected[i];
        char what[96];
        (void)snprintf(what, sizeof(what), "line %d decodes as expected", s_entries[i].line);

        CHECK(s_entries[i].line == e->line, "the frame is on the expected line");
        const obd_decode_outcome_t outcome = obd_decode_frame(&s_entries[i].frame);
        if (e->decoded) {
            const bool ok = outcome.decoded && strcmp(outcome.reading.name, e->name) == 0 &&
                            strcmp(outcome.reading.unit, e->unit) == 0 &&
                            fabs(outcome.reading.value - e->value) < 1e-6;
            CHECK(ok, what);
        } else {
            CHECK(!outcome.decoded && strcmp(obd_ignore_tag(outcome.reason), e->reason) == 0, what);
        }
    }
}

static hal_can_frame_t frame_of(uint32_t id, const uint8_t *bytes, uint8_t dlc)
{
    hal_can_frame_t frame = {0};
    frame.id = id;
    frame.dlc = dlc;
    memcpy(frame.data, bytes, dlc);
    return frame;
}

static void test_padding_after_the_declared_length_is_ignored(void)
{
    const uint8_t padded[8] = {0x04, 0x41, 0x0C, 0x1A, 0xF8, 0x55, 0x55, 0x55};
    const hal_can_frame_t frame = frame_of(0x7E8u, padded, 8u);
    const obd_decode_outcome_t outcome = obd_decode_frame(&frame);
    CHECK(outcome.decoded && fabs(outcome.reading.value - 1726.0) < 1e-9, "0x55 padding does not change the value");
}

static void test_negative_response_is_classified(void)
{
    const uint8_t nrc[8] = {0x03, 0x7F, 0x01, 0x12, 0x55, 0x55, 0x55, 0x55};
    const hal_can_frame_t frame = frame_of(0x7E8u, nrc, 8u);
    const obd_decode_outcome_t outcome = obd_decode_frame(&frame);
    CHECK(!outcome.decoded && outcome.reason == OBD_IGNORE_NEGATIVE_RESPONSE, "03 7F 01 12 is a negative response");
}

static void test_table_matches_its_lookup(void)
{
    const obd_pid_info_t *table = obd_pid_table();
    for (size_t i = 0; i < OBD_PID_COUNT; i++) {
        CHECK(obd_pid_info(table[i].pid) == &table[i], "every table row is found by its PID");
    }
    CHECK(obd_pid_info(0x00u) == NULL, "a support PID is not a decoded parameter");
}

/* ---------------------------------------------------------------------------------------- */
/* Support bitmaps                                                                           */
/* ---------------------------------------------------------------------------------------- */

static void test_bitmap_declares_pids_and_next_range(void)
{
    obd_support_t support;
    obd_support_init(&support);
    /* A typical engine ECU answer to PID 0x00. */
    const uint8_t data[4] = {0xBE, 0x3F, 0xA8, 0x13};
    CHECK(obd_support_apply(&support, 0x00u, data), "the bitmap of PID 0x00 is accepted");

    CHECK(obd_support_has(&support, 0x01u), "bit 7 of A declares PID 0x01");
    CHECK(!obd_support_has(&support, 0x02u), "a clear bit declares nothing");
    CHECK(obd_support_has(&support, 0x04u) && obd_support_has(&support, 0x05u), "engine load and coolant declared");
    CHECK(obd_support_has(&support, 0x0Cu) && obd_support_has(&support, 0x0Du), "engine speed and vehicle speed declared");
    CHECK(!obd_support_has(&support, 0x0Au), "fuel pressure not declared");
    CHECK(obd_support_has(&support, 0x20u), "bit 0 of D declares PID 0x20");
    CHECK(obd_support_declares_next(&support, 0x00u), "range 0x00 declares range 0x20");
    CHECK(!obd_support_has(&support, 0x2Fu), "a range never applied declares nothing");
    CHECK(!obd_support_declares_next(&support, 0x20u), "an unknown range declares no next range");
}

static void test_bitmap_without_next_range(void)
{
    obd_support_t support;
    obd_support_init(&support);
    const uint8_t data[4] = {0x00, 0x02, 0x00, 0x00};  /* only PID 0x2F in range 0x20 */
    CHECK(obd_support_apply(&support, 0x20u, data), "the bitmap of PID 0x20 is accepted");
    CHECK(obd_support_has(&support, 0x2Fu), "fuel level declared");
    CHECK(!obd_support_declares_next(&support, 0x20u), "a clear last bit declares no next range");
}

static void test_bitmap_declaring_nothing(void)
{
    obd_support_t support;
    obd_support_init(&support);
    const uint8_t data[4] = {0, 0, 0, 0};
    CHECK(obd_support_apply(&support, 0x00u, data), "an empty bitmap is accepted");
    int declared = 0;
    for (unsigned pid = 1u; pid <= 0x20u; pid++) {
        declared += obd_support_has(&support, (uint8_t)pid) ? 1 : 0;
    }
    CHECK(declared == 0, "an empty bitmap declares no PID");
    CHECK(!obd_support_declares_next(&support, 0x00u), "an empty bitmap declares no next range");
    CHECK(!obd_support_apply(&support, 0x10u, data), "0x10 is not a support PID");
}

static void test_support_answer_is_recognised(void)
{
    const uint8_t answer[8] = {0x06, 0x41, 0x00, 0xBE, 0x3F, 0xA8, 0x13, 0x55};
    hal_can_frame_t frame = frame_of(0x7E8u, answer, 8u);
    uint8_t range = 0xFFu;
    uint8_t data[4] = {0};
    CHECK(obd_parse_support_response(&frame, &range, data), "a support answer is recognised");
    CHECK(range == 0x00u && data[0] == 0xBE && data[3] == 0x13, "range and bitmap bytes are extracted");

    const uint8_t reading[8] = {0x04, 0x41, 0x0C, 0x1A, 0xF8, 0x55, 0x55, 0x55};
    frame = frame_of(0x7E8u, reading, 8u);
    CHECK(!obd_parse_support_response(&frame, &range, data), "an ordinary reading is not a support answer");

    frame = frame_of(0x7E0u, answer, 8u);
    CHECK(!obd_parse_support_response(&frame, &range, data), "a request identifier is not a response");
}

int obd_tests_run(void)
{
    s_failures = 0;
    s_checks = 0;

    printf("obd: %u parameters\n", (unsigned)OBD_PID_COUNT);

    test_fixture_decodes_to_its_expectation();
    test_padding_after_the_declared_length_is_ignored();
    test_negative_response_is_classified();
    test_table_matches_its_lookup();
    test_bitmap_declares_pids_and_next_range();
    test_bitmap_without_next_range();
    test_bitmap_declaring_nothing();
    test_support_answer_is_recognised();

    printf("%d checks, %d failures\n", s_checks, s_failures);
    return s_failures;
}
