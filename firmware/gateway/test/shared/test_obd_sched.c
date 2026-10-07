/**
 * @file test_obd_sched.c
 * @brief Scheduler and error-guard tests, shared by the host build and the target.
 *
 * A fake engine ECU answers the scheduler's requests after a configurable latency, and time
 * is advanced in fixed steps, so discovery, cycles and timeouts are checked to the
 * microsecond without a bus or a clock.
 */
#include "test_obd_sched.h"

#include <stdio.h>
#include <string.h>

#include "obd.h"
#include "obd_guard.h"
#include "obd_sched.h"

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

#define MAX_LOG 256

/* ---------------------------------------------------------------------------------------- */
/* A fake engine ECU and a time-stepping driver                                              */
/* ---------------------------------------------------------------------------------------- */

typedef struct {
    bool declared[0x61];      /**< PIDs 0x01..0x60 the bitmaps declare (0x20, 0x40 = next range). */
    bool silent[0x61];        /**< Declared, but never answered. */
    bool refused[0x61];       /**< Answered with a negative response. */
    bool silent_range[3];     /**< Support bitmap request never answered. */
    uint64_t latency_us;
} fake_ecu_t;

typedef struct {
    uint64_t at_us;
    hal_can_frame_t frame;
} sent_t;

typedef struct {
    uint64_t at_us;
    obd_event_t event;
    uint8_t pid;
} event_t;

typedef struct {
    sent_t sent[MAX_LOG];
    int sent_count;
    event_t events[MAX_LOG];
    int event_count;
    bool non_mode01;          /**< Any transmitted frame that is not a Mode 01 request. */
    uint32_t request_id;
    /* One answer in flight, as on a real bus where the ECU answers before the next request. */
    bool pending;
    uint64_t pending_at_us;
    hal_can_frame_t pending_frame;
} trace_t;

static hal_can_frame_t answer_frame(uint32_t id, uint8_t length, const uint8_t *payload)
{
    hal_can_frame_t frame = {0};
    frame.id = id;
    frame.dlc = 8u;
    frame.data[0] = length;
    for (uint8_t i = 0; i < 7u; i++) {
        frame.data[1 + i] = i < length ? payload[i] : 0x55u;
    }
    return frame;
}

/** The ECU's answer to one request, or false when it stays silent. */
static bool ecu_answer(const fake_ecu_t *ecu, const hal_can_frame_t *request, hal_can_frame_t *out)
{
    const uint8_t pid = request->data[2];
    if (obd_is_support_pid(pid)) {
        const int range = pid / 0x20;
        if (ecu->silent_range[range]) {
            return false;
        }
        uint8_t payload[6] = {0x41u, pid, 0, 0, 0, 0};
        for (unsigned bit = 0; bit < 32u; bit++) {
            const unsigned declared_pid = pid + 1u + bit;
            if (declared_pid <= 0x60u && ecu->declared[declared_pid]) {
                payload[2 + bit / 8u] |= (uint8_t)(0x80u >> (bit % 8u));
            }
        }
        *out = answer_frame(OBD_ENGINE_RESPONSE_ID, 6u, payload);
        return true;
    }
    if (pid > 0x60u || !ecu->declared[pid] || ecu->silent[pid]) {
        return false;
    }
    if (ecu->refused[pid]) {
        const uint8_t nrc[3] = {0x7Fu, 0x01u, 0x12u};
        *out = answer_frame(OBD_ENGINE_RESPONSE_ID, 3u, nrc);
        return true;
    }
    const obd_pid_info_t *info = obd_pid_info(pid);
    const uint8_t bytes = info != NULL ? info->min_bytes : 1u;
    const uint8_t payload[4] = {0x41u, pid, 0x40u, 0x00u};
    *out = answer_frame(OBD_ENGINE_RESPONSE_ID, (uint8_t)(2u + bytes), payload);
    return true;
}

static void record(trace_t *trace, const fake_ecu_t *ecu, uint64_t now_us, const obd_sched_outcome_t *out)
{
    if (out->event != OBD_EVENT_NONE && trace->event_count < MAX_LOG) {
        trace->events[trace->event_count++] = (event_t){now_us, out->event, out->pid};
    }
    if (!out->send_frame) {
        return;
    }
    if (trace->sent_count < MAX_LOG) {
        trace->sent[trace->sent_count++] = (sent_t){now_us, out->frame};
    }
    const hal_can_frame_t *f = &out->frame;
    if (f->id != trace->request_id || f->extended || f->data[0] != 0x02u || f->data[1] != 0x01u) {
        trace->non_mode01 = true;
    }
    hal_can_frame_t answer;
    if (ecu != NULL && ecu_answer(ecu, f, &answer)) {
        trace->pending = true;
        trace->pending_at_us = now_us + ecu->latency_us;
        trace->pending_frame = answer;
    }
}

/** Advance from @p from_us to @p to_us in @p step_us steps, delivering due answers. */
static void run(obd_sched_t *sched, trace_t *trace, const fake_ecu_t *ecu, uint64_t from_us, uint64_t to_us,
                uint64_t step_us)
{
    for (uint64_t now = from_us; now <= to_us; now += step_us) {
        obd_sched_outcome_t out;
        if (trace->pending && trace->pending_at_us <= now) {
            trace->pending = false;
            const hal_can_frame_t answer = trace->pending_frame;
            obd_sched_on_frame(sched, &answer, now, &out);
            record(trace, ecu, now, &out);
        }
        obd_sched_on_tick(sched, now, &out);
        record(trace, ecu, now, &out);
    }
}

static void begin(obd_sched_t *sched, trace_t *trace, const obd_sched_config_t *config)
{
    memset(trace, 0, sizeof(*trace));
    obd_sched_init(sched, config);
    trace->request_id = sched->config.request_id;
}

static int requests_for(const trace_t *trace, uint8_t pid)
{
    int n = 0;
    for (int i = 0; i < trace->sent_count; i++) {
        n += trace->sent[i].frame.data[2] == pid ? 1 : 0;
    }
    return n;
}

static int events_of(const trace_t *trace, obd_event_t event)
{
    int n = 0;
    for (int i = 0; i < trace->event_count; i++) {
        n += trace->events[i].event == event ? 1 : 0;
    }
    return n;
}

static fake_ecu_t ecu_with(const uint8_t *pids, int count)
{
    fake_ecu_t ecu;
    memset(&ecu, 0, sizeof(ecu));
    ecu.latency_us = 5000u;
    for (int i = 0; i < count; i++) {
        ecu.declared[pids[i]] = true;
    }
    return ecu;
}

static obd_sched_t s_sched;
static trace_t s_trace;

/* ---------------------------------------------------------------------------------------- */
/* Scheduler                                                                                 */
/* ---------------------------------------------------------------------------------------- */

static void test_only_supported_parameters_are_polled(void)
{
    const uint8_t pids[] = {0x05u, 0x0Cu};  /* coolant and engine speed, no next range */
    const fake_ecu_t ecu = ecu_with(pids, 2);
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 3500000u, 1000u);

    CHECK(events_of(&s_trace, OBD_EVENT_DISCOVERED) == 1, "discovery completes once");
    CHECK(requests_for(&s_trace, 0x05u) == 4 && requests_for(&s_trace, 0x0Cu) == 4,
          "each supported PID is requested once per cycle, four cycles in 3.5 s");
    CHECK(requests_for(&s_trace, 0x2Fu) == 0, "fuel level is never requested");
    CHECK(obd_sched_polls(&s_sched, 0x05u) && !obd_sched_polls(&s_sched, 0x0Du), "the poll list is the supported set");
    CHECK(events_of(&s_trace, OBD_EVENT_READING) == 8, "every answer is a reading");
    CHECK(!s_trace.non_mode01, "every transmitted frame is a Mode 01 request to 0x7E0");
}

static void test_undeclared_range_is_not_queried(void)
{
    const uint8_t pids[] = {0x0Cu};
    const fake_ecu_t ecu = ecu_with(pids, 1);
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 1500000u, 1000u);

    CHECK(requests_for(&s_trace, 0x00u) == 1, "PID 0x00 is asked once");
    CHECK(requests_for(&s_trace, 0x20u) == 0 && requests_for(&s_trace, 0x40u) == 0,
          "ranges the bitmap does not declare are not asked");
}

static void test_declared_ranges_are_followed(void)
{
    const uint8_t pids[] = {0x0Cu, 0x20u, 0x2Fu, 0x40u, 0x42u, 0x5Eu};
    const fake_ecu_t ecu = ecu_with(pids, 6);
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 500000u, 1000u);

    CHECK(s_trace.sent_count >= 3 && s_trace.sent[0].frame.data[2] == 0x00u &&
              s_trace.sent[1].frame.data[2] == 0x20u && s_trace.sent[2].frame.data[2] == 0x40u,
          "the three bitmaps are asked in order");
    CHECK(obd_sched_polls(&s_sched, 0x2Fu) && obd_sched_polls(&s_sched, 0x42u) && obd_sched_polls(&s_sched, 0x5Eu),
          "PIDs declared in later ranges are polled");
}

static void test_failed_discovery_requests_nothing(void)
{
    fake_ecu_t ecu = ecu_with(NULL, 0);
    ecu.silent_range[0] = true;
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 2000000u, 1000u);

    CHECK(requests_for(&s_trace, 0x00u) == 3, "PID 0x00 is asked the configured three times");
    CHECK(s_trace.sent_count == 3, "nothing else is ever requested");
    CHECK(events_of(&s_trace, OBD_EVENT_DISCOVERY_FAILED) == 1, "the failure is reported once");
    CHECK(s_sched.state == OBD_SCHED_DISCOVERY_FAILED, "the scheduler stays failed");
}

static void test_silent_later_range_keeps_the_earlier_ones(void)
{
    const uint8_t pids[] = {0x0Cu, 0x20u, 0x2Fu};
    fake_ecu_t ecu = ecu_with(pids, 3);
    ecu.silent_range[1] = true;
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 500000u, 1000u);

    CHECK(requests_for(&s_trace, 0x20u) == 3, "the silent range is asked three times");
    CHECK(events_of(&s_trace, OBD_EVENT_DISCOVERED) == 1, "discovery still completes");
    CHECK(obd_sched_polls(&s_sched, 0x0Cu) && !obd_sched_polls(&s_sched, 0x2Fu),
          "what range 0x00 declared is polled, nothing from the silent range");
}

static void test_cycle_visits_each_parameter_once_in_order(void)
{
    const uint8_t pids[] = {0x04u, 0x05u, 0x0Cu, 0x0Du, 0x11u};
    const fake_ecu_t ecu = ecu_with(pids, 5);
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 900000u, 1000u);

    /* One discovery request, then one cycle of five. */
    CHECK(s_trace.sent_count == 6, "a cycle sends exactly one request per supported PID");
    bool ordered = s_trace.sent_count == 6;
    for (int i = 1; i < s_trace.sent_count && ordered; i++) {
        ordered = s_trace.sent[i].frame.data[2] == pids[i - 1];
        /* Each request goes out only after the previous one was answered (5 ms latency). */
        ordered = ordered && s_trace.sent[i].at_us >= s_trace.sent[i - 1].at_us + ecu.latency_us;
    }
    CHECK(ordered, "requests follow the table order, each after the previous answer");
}

static void test_timeout_is_counted_and_skipped(void)
{
    const uint8_t pids[] = {0x05u, 0x0Du, 0x11u};
    fake_ecu_t ecu = ecu_with(pids, 3);
    ecu.silent[0x0Du] = true;
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 900000u, 1000u);

    CHECK(s_sched.counters.timeouts == 1u, "the silent PID costs one timeout per cycle");
    int at = -1;
    for (int i = 0; i < s_trace.sent_count; i++) {
        if (s_trace.sent[i].frame.data[2] == 0x0Du) {
            at = i;
        }
    }
    CHECK(at > 0 && at + 1 < s_trace.sent_count, "a request follows the silent one");
    if (at > 0 && at + 1 < s_trace.sent_count) {
        CHECK(s_trace.sent[at + 1].at_us - s_trace.sent[at].at_us == OBD_RESPONSE_TIMEOUT_US,
              "the next PID is requested exactly 50 ms later");
        CHECK(s_trace.sent[at + 1].frame.data[2] == 0x11u, "the next PID is the following one");
    }
}

static void test_period_does_not_drift(void)
{
    const uint8_t pids[] = {0x04u, 0x05u, 0x0Cu};
    fake_ecu_t ecu = ecu_with(pids, 3);
    ecu.latency_us = 10000u;  /* 30 ms of answers per cycle */
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 5500000u, 1000u);

    uint64_t starts[8];
    int n = 0;
    for (int i = 0; i < s_trace.sent_count && n < 8; i++) {
        if (s_trace.sent[i].frame.data[2] == 0x04u) {
            starts[n++] = s_trace.sent[i].at_us;
        }
    }
    CHECK(n >= 5, "at least five cycles ran");
    bool steady = n >= 5;
    for (int i = 1; i < n; i++) {
        steady = steady && starts[i] - starts[i - 1] == 1000000u;
    }
    CHECK(steady, "every cycle starts exactly one period after the previous one");
}

static void test_negative_response_is_counted(void)
{
    const uint8_t pids[] = {0x05u, 0x10u, 0x11u};
    fake_ecu_t ecu = ecu_with(pids, 3);
    ecu.refused[0x10u] = true;
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 900000u, 1000u);

    CHECK(s_sched.counters.negative == 1u, "the refusal is counted");
    CHECK(s_sched.counters.timeouts == 0u, "a refusal is not a timeout");
    CHECK(requests_for(&s_trace, 0x11u) == 1, "the next PID is requested after the refusal");
}

static void test_other_ecu_answers_are_ignored(void)
{
    const uint8_t pids[] = {0x0Du};
    const fake_ecu_t ecu = ecu_with(pids, 1);
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 6000u, 1000u);  /* discovery answered at 5 ms, 0x0D requested */

    const uint8_t speed[3] = {0x41u, 0x0Du, 0x3Eu};
    hal_can_frame_t other = answer_frame(0x7E9u, 3u, speed);
    obd_sched_outcome_t out;
    obd_sched_on_frame(&s_sched, &other, 7000u, &out);
    CHECK(out.event == OBD_EVENT_OTHER_ECU && !out.send_frame, "an answer from 0x7E9 is another ECU's");
    CHECK(s_sched.state == OBD_SCHED_POLL_WAIT, "the scheduler still waits for the engine ECU");

    hal_can_frame_t engine = answer_frame(OBD_ENGINE_RESPONSE_ID, 3u, speed);
    obd_sched_on_frame(&s_sched, &engine, 8000u, &out);
    CHECK(out.event == OBD_EVENT_READING && out.reading.value == 62.0, "the engine ECU's answer is the reading");
}

static void test_functional_addressing(void)
{
    const uint8_t pids[] = {0x0Cu};
    const fake_ecu_t ecu = ecu_with(pids, 1);
    obd_sched_config_t config = obd_sched_default_config();
    config.request_id = OBD_FUNCTIONAL_REQUEST_ID;
    begin(&s_sched, &s_trace, &config);
    run(&s_sched, &s_trace, &ecu, 0u, 100000u, 1000u);

    CHECK(s_trace.sent_count == 2 && s_trace.sent[1].frame.id == OBD_FUNCTIONAL_REQUEST_ID,
          "requests go to 0x7DF");
    CHECK(events_of(&s_trace, OBD_EVENT_READING) == 1, "answers are still taken from 0x7E8");
    CHECK(!s_trace.non_mode01, "every transmitted frame is a Mode 01 request");
}

static void test_stop_silences_the_scheduler(void)
{
    const uint8_t pids[] = {0x0Cu};
    const fake_ecu_t ecu = ecu_with(pids, 1);
    begin(&s_sched, &s_trace, NULL);
    run(&s_sched, &s_trace, &ecu, 0u, 100000u, 1000u);
    const int before = s_trace.sent_count;
    obd_sched_stop(&s_sched);
    run(&s_sched, &s_trace, &ecu, 101000u, 5000000u, 1000u);
    CHECK(s_trace.sent_count == before, "nothing is requested after stop");
    CHECK(s_sched.state == OBD_SCHED_STOPPED, "the scheduler stays stopped");
}

/* ---------------------------------------------------------------------------------------- */
/* Error guard                                                                               */
/* ---------------------------------------------------------------------------------------- */

static void test_guard(void)
{
    CHECK(!obd_guard_should_stop(OBD_TARGET_VEHICLE, 16u, 16u, false), "a vehicle at the threshold keeps polling");
    CHECK(obd_guard_should_stop(OBD_TARGET_VEHICLE, 17u, 16u, false), "a vehicle above the threshold stops");
    CHECK(obd_guard_should_stop(OBD_TARGET_VEHICLE, 0u, 16u, true), "a vehicle bus-off stops");
    CHECK(!obd_guard_should_stop(OBD_TARGET_BENCH, 1000u, 16u, false), "the bench never stops on errors");
    CHECK(!obd_guard_should_stop(OBD_TARGET_BENCH, 0u, 16u, true), "the bench recovers from bus-off instead");
}

int obd_sched_tests_run(void)
{
    s_failures = 0;
    s_checks = 0;

    printf("obd scheduler: %u us timeout\n", (unsigned)OBD_RESPONSE_TIMEOUT_US);

    test_only_supported_parameters_are_polled();
    test_undeclared_range_is_not_queried();
    test_declared_ranges_are_followed();
    test_failed_discovery_requests_nothing();
    test_silent_later_range_keeps_the_earlier_ones();
    test_cycle_visits_each_parameter_once_in_order();
    test_timeout_is_counted_and_skipped();
    test_period_does_not_drift();
    test_negative_response_is_counted();
    test_other_ecu_answers_are_ignored();
    test_functional_addressing();
    test_stop_silences_the_scheduler();
    test_guard();

    printf("%d checks, %d failures\n", s_checks, s_failures);
    return s_failures;
}
