/**
 * @file main.c
 * @brief Gateway application: read OBD-II Mode 01 data from the engine ECU and print it.
 *
 * Two ways to run, chosen at build time (main/Kconfig.projbuild):
 *  - listen-only, the safe first contact with a vehicle: nothing is transmitted or
 *    acknowledged, and every 5 s the console shows which identifiers were seen;
 *  - polling: the scheduler discovers the supported PIDs and asks for them in cycles, and
 *    every reading is printed as one parseable line.
 *
 * On a vehicle target, bus errors above the threshold or a bus-off stop all requests for
 * good: the link is restarted in listen-only mode until the device is reset.
 *
 * This task is the low-priority consumer; the link layer's receive task outranks it, so
 * printing never delays reception.
 */
#include <inttypes.h>
#include <stdio.h>

#include "can_link.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "obd.h"
#include "obd_guard.h"
#include "obd_sched.h"
#include "sdkconfig.h"

static const char *TAG = "gateway";

#define LOOP_PERIOD_MS 1u
#define STATS_PERIOD_US 5000000u
#define TRANSMIT_TIMEOUT_MS 10u
#define MAX_OBSERVED_IDS 32

/* OBD response identifiers 0x7E8 to 0x7EF: compare all but the low three bits. */
#define OBD_RESPONSE_FILTER_ID 0x7E8u
#define OBD_RESPONSE_FILTER_MASK 0x7F8u

#if CONFIG_GATEWAY_TARGET_VEHICLE
#define GATEWAY_TARGET OBD_TARGET_VEHICLE
#define GATEWAY_TARGET_NAME "vehicle"
#else
#define GATEWAY_TARGET OBD_TARGET_BENCH
#define GATEWAY_TARGET_NAME "bench"
#endif

#if CONFIG_GATEWAY_CAN_BITRATE_250
#define GATEWAY_BITRATE_KBPS 250u
#else
#define GATEWAY_BITRATE_KBPS 500u
#endif

#if CONFIG_GATEWAY_LISTEN_ONLY
#define GATEWAY_LISTEN_ONLY true
#else
#define GATEWAY_LISTEN_ONLY false
#endif

#if CONFIG_GATEWAY_FUNCTIONAL_ADDRESSING
#define GATEWAY_REQUEST_ID OBD_FUNCTIONAL_REQUEST_ID
#else
#define GATEWAY_REQUEST_ID OBD_ENGINE_REQUEST_ID
#endif

static const char *const STATE_NAME[] = {"stopped", "running", "bus-off", "recovering"};

/* ---------------------------------------------------------------------------------------- */
/* Listen-only observation                                                                   */
/* ---------------------------------------------------------------------------------------- */

typedef struct {
    uint32_t id;
    bool extended;
    uint32_t frames;
} observed_id_t;

static observed_id_t s_observed[MAX_OBSERVED_IDS];
static int s_observed_count;
static uint32_t s_observed_overflow;

static void observe(const hal_can_frame_t *frame)
{
    for (int i = 0; i < s_observed_count; i++) {
        if (s_observed[i].id == frame->id && s_observed[i].extended == frame->extended) {
            s_observed[i].frames++;
            return;
        }
    }
    if (s_observed_count < MAX_OBSERVED_IDS) {
        s_observed[s_observed_count++] = (observed_id_t){frame->id, frame->extended, 1u};
    } else {
        s_observed_overflow++;  /* more identifiers than the table holds; counted, not lost silently */
    }
}

static void print_observed(const char *why)
{
    can_link_stats_t stats;
    can_link_get_stats(&stats);
    ESP_LOGI(TAG,
             "listen-only%s | frames=%" PRIu32 " ids=%d overflow=%" PRIu32 " bus_errors=%" PRIu32
             " dropped=%" PRIu32 " state=%s",
             why, stats.rx_frames, s_observed_count, s_observed_overflow, stats.bus_errors, stats.dropped,
             STATE_NAME[stats.state]);
    for (int i = 0; i < s_observed_count; i++) {
        printf("ID,%s,0x%0*" PRIX32 ",%" PRIu32 "\n", s_observed[i].extended ? "ext" : "std",
               s_observed[i].extended ? 8 : 3, s_observed[i].id, s_observed[i].frames);
    }
}

/** Observe the bus for good. Used for first contact and after the guard stopped requests. */
static void listen_forever(const char *why)
{
    uint64_t next_report = hal_can_now_us() + STATS_PERIOD_US;
    while (true) {
        hal_can_frame_t frame;
        while (can_link_receive(&frame)) {
            observe(&frame);
        }
        const uint64_t now = hal_can_now_us();
        if (now >= next_report) {
            next_report = now + STATS_PERIOD_US;
            print_observed(why);
        }
        vTaskDelay(pdMS_TO_TICKS(LOOP_PERIOD_MS));
    }
}

static bool start_link(bool listen_only)
{
    can_link_config_t config = can_link_default_config();
    config.bitrate_kbps = GATEWAY_BITRATE_KBPS;
    config.listen_only = listen_only;
    /* A vehicle bus-off is left alone; the guard reacts to it instead. */
    config.auto_recover = GATEWAY_TARGET == OBD_TARGET_BENCH;
    if (listen_only) {
        config.filter_mode = HAL_CAN_FILTER_ACCEPT_ALL;  /* see everything the port carries */
    } else {
        config.filter_mode = HAL_CAN_FILTER_MASK;
        config.filter_id = OBD_RESPONSE_FILTER_ID;
        config.filter_mask = OBD_RESPONSE_FILTER_MASK;
    }
    if (can_link_start(&config) != CAN_LINK_OK) {
        /* Almost always wiring: check the transceiver, common ground and termination before
         * suspecting the firmware. */
        ESP_LOGE(TAG, "link layer did not start; check wiring and transceiver power");
        return false;
    }
    return true;
}

/* ---------------------------------------------------------------------------------------- */
/* Polling                                                                                   */
/* ---------------------------------------------------------------------------------------- */

static uint32_t s_tx_failed;

static void print_reading(const obd_sched_outcome_t *out, uint64_t received_us)
{
    printf("OBD,%" PRIu64 ",0x%02X,%s,%.2f,%s,0x%03" PRIX32 "\n", received_us / 1000u, out->reading.pid,
           out->reading.name, out->reading.value, out->reading.unit, out->source_id);
}

static void print_discovery(const obd_sched_t *sched)
{
    char list[OBD_PID_COUNT * 5 + 1];
    int offset = 0;
    for (uint8_t i = 0; i < sched->poll_count; i++) {
        offset += snprintf(list + offset, sizeof(list) - (size_t)offset, " 0x%02X", sched->poll_pids[i]);
    }
    list[offset] = '\0';
    ESP_LOGI(TAG, "discovered %u supported PIDs:%s", (unsigned)sched->poll_count, list);
}

static void handle(obd_sched_t *sched, const obd_sched_outcome_t *out, uint64_t received_us)
{
    switch (out->event) {
    case OBD_EVENT_READING:
        print_reading(out, received_us);
        break;
    case OBD_EVENT_DISCOVERED:
        print_discovery(sched);
        break;
    case OBD_EVENT_DISCOVERY_FAILED:
        ESP_LOGW(TAG, "discovery failed: the engine ECU answered no usable support bitmap");
        break;
    default:
        break;
    }

    if (out->send_frame && can_link_transmit(&out->frame, TRANSMIT_TIMEOUT_MS) != CAN_LINK_OK) {
        s_tx_failed++;
    }
}

static void print_counters(const obd_sched_t *sched, uint32_t bus_errors_since_start)
{
    const obd_sched_counters_t *c = &sched->counters;
    can_link_stats_t stats;
    can_link_get_stats(&stats);

    uint32_t ignored = 0u;
    for (int i = 0; i < OBD_IGNORE_REASON_COUNT; i++) {
        ignored += c->ignored[i];
    }
    ESP_LOGI(TAG,
             "stats | cycles=%" PRIu32 " requests=%" PRIu32 " answers=%" PRIu32 " timeouts=%" PRIu32
             " negative=%" PRIu32 " other_ecu=%" PRIu32 " unexpected=%" PRIu32 " ignored=%" PRIu32
             " tx_failed=%" PRIu32 " bus_errors=%" PRIu32 " dropped=%" PRIu32 " recoveries=%" PRIu32
             " state=%s",
             c->cycles, c->requests, c->answers, c->timeouts, c->negative, c->other_ecu, c->unexpected, ignored,
             s_tx_failed, bus_errors_since_start, stats.dropped, stats.recoveries, STATE_NAME[stats.state]);
    for (int i = 1; i < OBD_IGNORE_REASON_COUNT; i++) {
        if (c->ignored[i] > 0u) {
            ESP_LOGI(TAG, "  ignored %s=%" PRIu32, obd_ignore_tag((obd_ignore_reason_t)i), c->ignored[i]);
        }
    }
}

static void poll_forever(void)
{
    static obd_sched_t sched;  /* static: no allocation, and kept off the small main stack */
    obd_sched_config_t config = obd_sched_default_config();
    config.request_id = GATEWAY_REQUEST_ID;
    config.period_us = (uint64_t)CONFIG_GATEWAY_POLL_PERIOD_MS * 1000u;
    obd_sched_init(&sched, &config);

    can_link_stats_t stats;
    can_link_get_stats(&stats);
    const uint32_t errors_at_start = stats.bus_errors;
    uint64_t next_report = hal_can_now_us() + STATS_PERIOD_US;

    while (true) {
        obd_sched_outcome_t out;
        hal_can_frame_t frame;
        while (can_link_receive(&frame)) {
            obd_sched_on_frame(&sched, &frame, hal_can_now_us(), &out);
            handle(&sched, &out, frame.timestamp_us);
        }

        const uint64_t now = hal_can_now_us();
        obd_sched_on_tick(&sched, now, &out);
        handle(&sched, &out, now);

        can_link_get_stats(&stats);
        const uint32_t errors = stats.bus_errors - errors_at_start;
        if (obd_guard_should_stop(GATEWAY_TARGET, errors, CONFIG_GATEWAY_ERROR_GUARD_THRESHOLD,
                                  stats.state == HAL_CAN_STATE_BUS_OFF)) {
            obd_sched_stop(&sched);
            print_counters(&sched, errors);
            ESP_LOGE(TAG, "requests stopped for good: %s on a vehicle bus; listening only until reset",
                     stats.state == HAL_CAN_STATE_BUS_OFF ? "bus-off" : "bus errors above the threshold");
            (void)can_link_stop();
            if (!start_link(true)) {
                return;
            }
            listen_forever(" (requests stopped by the error guard)");
        }

        if (now >= next_report) {
            next_report = now + STATS_PERIOD_US;
            print_counters(&sched, errors);
        }
        vTaskDelay(pdMS_TO_TICKS(LOOP_PERIOD_MS));
    }
}

void app_main(void)
{
    ESP_LOGI(TAG, "vehicle diagnostics gateway, OBD-II Mode 01 client");
    ESP_LOGI(TAG, "target=%s listen_only=%s bitrate=%u kbps requests_on=0x%03X period=%d ms guard=%d errors",
             GATEWAY_TARGET_NAME, GATEWAY_LISTEN_ONLY ? "yes" : "no", (unsigned)GATEWAY_BITRATE_KBPS,
             (unsigned)GATEWAY_REQUEST_ID, CONFIG_GATEWAY_POLL_PERIOD_MS, CONFIG_GATEWAY_ERROR_GUARD_THRESHOLD);

    if (!start_link(GATEWAY_LISTEN_ONLY)) {
        return;
    }
    if (GATEWAY_LISTEN_ONLY) {
        ESP_LOGW(TAG, "listen-only: nothing is transmitted or acknowledged on this bus");
        listen_forever("");
    }
    poll_forever();
}
