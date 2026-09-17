/**
 * @file main.c
 * @brief Phase F0 application: prove a frame crosses the bench in both directions.
 *
 * Sends a request frame once a second and prints everything that arrives, plus the bus
 * counters. No transport, no diagnostics, no networking: those are later phases. The task
 * split is already the one later phases need, so their timing behaviour is not a surprise.
 */
#include <inttypes.h>
#include <stdio.h>

#include "can_link.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "gateway";

/*
 * Bench identifiers, matching firmware/sim-ecu/sim-ecu.ino. These are the real OBD-II
 * diagnostic identifiers, so the wiring proven here stays valid when phase F1 puts an actual
 * protocol on top. At F0 they carry no protocol meaning.
 */
#define BENCH_REQUEST_ID 0x7DFu  /* Functional request, tester to all ECUs. */
#define BENCH_RESPONSE_ID 0x7E8u /* Physical response, first ECU to tester. */

#define REQUEST_PERIOD_MS 1000u
#define STATS_PERIOD_MS 5000u
#define CONSUMER_TASK_PRIORITY 5u
#define CONSUMER_TASK_STACK_BYTES 4096

static void log_frame(const hal_can_frame_t *frame)
{
    char hex[HAL_CAN_MAX_DLC * 3 + 1];
    int offset = 0;
    for (uint8_t i = 0; i < frame->dlc; i++) {
        offset += snprintf(hex + offset, sizeof(hex) - (size_t)offset, "%02X ", frame->data[i]);
    }
    hex[offset > 0 ? offset - 1 : 0] = '\0';

    ESP_LOGI(TAG, "RX %s 0x%03" PRIX32 " [%u] %s  t=%" PRIu64 " us",
             frame->extended ? "ext" : "std", frame->id, (unsigned)frame->dlc, hex,
             frame->timestamp_us);

    if (frame->id == BENCH_RESPONSE_ID) {
        ESP_LOGI(TAG, "  ^ simulator answered: the bench round trip works");
    }
}

/**
 * Low-priority consumer. Stands in for the publishing path of later phases, and exists now so
 * that formatting and printing are already off the receive task's critical path.
 */
static void consumer_task(void *arg)
{
    (void)arg;
    while (true) {
        hal_can_frame_t frame;
        if (can_link_receive(&frame)) {
            log_frame(&frame);
        } else {
            vTaskDelay(pdMS_TO_TICKS(5));
        }
    }
}

static void print_stats(void)
{
    can_link_stats_t stats;
    can_link_get_stats(&stats);

    static const char *const state_name[] = {"stopped", "running", "bus-off", "recovering"};
    ESP_LOGI(TAG,
             "bus %s | rx=%" PRIu32 " tx=%" PRIu32 " buffered=%" PRIu32 " dropped=%" PRIu32
             " missed=%" PRIu32 " errors=%" PRIu32 " recoveries=%" PRIu32,
             state_name[stats.state], stats.rx_frames, stats.tx_frames, stats.buffered,
             stats.dropped, stats.rx_missed, stats.bus_errors, stats.recoveries);
}

void app_main(void)
{
    ESP_LOGI(TAG, "vehicle diagnostics gateway, phase F0 bring-up");

    const can_link_config_t config = can_link_default_config();
    if (can_link_start(&config) != CAN_LINK_OK) {
        /* Almost always wiring: check the transceiver, common ground and termination before
         * suspecting the firmware. */
        ESP_LOGE(TAG, "link layer did not start; check wiring and transceiver power");
        return;
    }

    if (xTaskCreate(consumer_task, "consumer", CONSUMER_TASK_STACK_BYTES, NULL,
                    CONSUMER_TASK_PRIORITY, NULL) != pdPASS) {
        ESP_LOGE(TAG, "could not create consumer task");
        return;
    }

    hal_can_frame_t request = {
        .id = BENCH_REQUEST_ID,
        .extended = false,
        .dlc = 8u,
        /* Shaped like an OBD-II Mode 01 request for engine speed, padded as the standard does. */
        .data = {0x02u, 0x01u, 0x0Cu, 0x55u, 0x55u, 0x55u, 0x55u, 0x55u},
    };

    uint32_t elapsed_ms = 0u;
    while (true) {
        const can_link_err_t err = can_link_transmit(&request, 100u);
        if (err == CAN_LINK_ERR_TIMEOUT) {
            /* Nobody acknowledged. On CAN that means no other powered node is listening. */
            ESP_LOGW(TAG, "transmit timed out: is the simulator powered and the bus terminated?");
        } else if (err != CAN_LINK_OK) {
            ESP_LOGE(TAG, "transmit failed (%d)", (int)err);
        }

        vTaskDelay(pdMS_TO_TICKS(REQUEST_PERIOD_MS));
        elapsed_ms += REQUEST_PERIOD_MS;

        if (elapsed_ms % STATS_PERIOD_MS == 0u) {
            print_stats();
        }
    }
}
