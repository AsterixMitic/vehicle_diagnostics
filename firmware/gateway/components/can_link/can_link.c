#include "can_link.h"

#include "can_ring_buffer.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "can_link";

/** Long enough that an idle bus costs almost nothing, short enough to notice a stop request. */
#define RX_POLL_TIMEOUT_MS 100u
#define RX_TASK_STACK_BYTES 3072

static can_ring_buffer_t s_buffer;
static TaskHandle_t s_rx_task;
static volatile bool s_running;
static volatile uint32_t s_recoveries;
static bool s_auto_recover;
/** A recovery was initiated and the controller has not been started again yet. */
static bool s_recovering;

can_link_config_t can_link_default_config(void)
{
    can_link_config_t config = {
        .tx_gpio = 5,  /* architecture.md §3: GPIO5 → CTX */
        .rx_gpio = 4,  /* architecture.md §3: GPIO4 → CRX */
        .bitrate_kbps = 500u,
        .filter_mode = HAL_CAN_FILTER_ACCEPT_ALL,
        .filter_id = 0u,
        .filter_mask = 0u,
        .task_priority = configMAX_PRIORITIES - 2u,
        .auto_recover = true,
        .listen_only = false,
    };
    return config;
}

/**
 * Highest-priority task in the system. It does exactly one thing: move frames out of the
 * driver before the driver queue overflows. No formatting, no logging per frame, no blocking
 * on a consumer. Anything slower belongs behind the ring buffer.
 */
static void can_rx_task(void *arg)
{
    (void)arg;
    ESP_LOGI(TAG, "receive task running");

    while (s_running) {
        hal_can_frame_t frame;
        const hal_can_err_t err = hal_can_receive(&frame, RX_POLL_TIMEOUT_MS);

        if (err == HAL_CAN_OK) {
            /* Return value deliberately ignored: a full buffer is already counted inside, and
             * the bus must not be stalled to deal with a slow consumer. */
            (void)can_ring_buffer_push(&s_buffer, &frame);
            continue;
        }

        if (err == HAL_CAN_ERR_TIMEOUT) {
            /* Idle bus. Use the quiet moment to check the controller is still healthy. */
            const hal_can_state_t state = hal_can_get_state();
            if (s_auto_recover && state == HAL_CAN_STATE_BUS_OFF && !s_recovering) {
                if (hal_can_recover() == HAL_CAN_OK) {
                    s_recovering = true;
                }
            } else if (s_recovering && state == HAL_CAN_STATE_STOPPED) {
                /* The driver stops once recovery completes; traffic resumes only on start. */
                if (hal_can_start() == HAL_CAN_OK) {
                    s_recovering = false;
                    s_recoveries++;
                    ESP_LOGI(TAG, "recovered from bus-off, controller running again");
                }
            }
            continue;
        }

        /* A genuine error is already counted by the layer below; do not spin hot on it. */
        vTaskDelay(pdMS_TO_TICKS(10));
    }

    ESP_LOGI(TAG, "receive task stopping");
    s_rx_task = NULL;
    vTaskDelete(NULL);
}

can_link_err_t can_link_start(const can_link_config_t *config)
{
    if (config == NULL) {
        return CAN_LINK_ERR_INVALID_ARG;
    }
    if (s_running) {
        return CAN_LINK_ERR_INVALID_STATE;
    }

    can_ring_buffer_init(&s_buffer);
    s_recoveries = 0u;
    s_auto_recover = config->auto_recover;
    s_recovering = false;

    const hal_can_config_t hal_config = {
        .tx_gpio = config->tx_gpio,
        .rx_gpio = config->rx_gpio,
        .bitrate_kbps = config->bitrate_kbps,
        .filter_mode = config->filter_mode,
        .filter_id = config->filter_id,
        .filter_mask = config->filter_mask,
        .listen_only = config->listen_only,
    };

    if (hal_can_init(&hal_config) != HAL_CAN_OK) {
        return CAN_LINK_ERR_FAILED;
    }
    if (hal_can_start() != HAL_CAN_OK) {
        (void)hal_can_stop();
        return CAN_LINK_ERR_FAILED;
    }

    s_running = true;
    const BaseType_t created = xTaskCreate(can_rx_task, "can_rx", RX_TASK_STACK_BYTES, NULL,
                                           (UBaseType_t)config->task_priority, &s_rx_task);
    if (created != pdPASS) {
        s_running = false;
        (void)hal_can_stop();
        ESP_LOGE(TAG, "could not create receive task");
        return CAN_LINK_ERR_FAILED;
    }

    ESP_LOGI(TAG, "started at %u kbps, buffer holds %u frames",
             (unsigned)config->bitrate_kbps, (unsigned)CAN_RING_BUFFER_USABLE);
    return CAN_LINK_OK;
}

can_link_err_t can_link_stop(void)
{
    if (!s_running) {
        return CAN_LINK_ERR_INVALID_STATE;
    }

    s_running = false;
    /* Let the task observe the flag and exit before the driver disappears underneath it. */
    for (int i = 0; i < 20 && s_rx_task != NULL; i++) {
        vTaskDelay(pdMS_TO_TICKS(RX_POLL_TIMEOUT_MS / 5u));
    }

    return hal_can_stop() == HAL_CAN_OK ? CAN_LINK_OK : CAN_LINK_ERR_FAILED;
}

bool can_link_receive(hal_can_frame_t *out_frame)
{
    return can_ring_buffer_pop(&s_buffer, out_frame);
}

can_link_err_t can_link_transmit(const hal_can_frame_t *frame, uint32_t timeout_ms)
{
    if (!s_running) {
        return CAN_LINK_ERR_INVALID_STATE;
    }

    switch (hal_can_transmit(frame, timeout_ms)) {
    case HAL_CAN_OK:
        return CAN_LINK_OK;
    case HAL_CAN_ERR_INVALID_ARG:
        return CAN_LINK_ERR_INVALID_ARG;
    case HAL_CAN_ERR_TIMEOUT:
        return CAN_LINK_ERR_TIMEOUT;
    default:
        return CAN_LINK_ERR_FAILED;
    }
}

void can_link_get_stats(can_link_stats_t *out_stats)
{
    if (out_stats == NULL) {
        return;
    }

    hal_can_counters_t counters = {0};
    (void)hal_can_get_counters(&counters);

    out_stats->rx_frames = counters.rx_frames;
    out_stats->tx_frames = counters.tx_frames;
    out_stats->bus_errors = counters.bus_errors;
    out_stats->rx_missed = counters.rx_missed;
    out_stats->buffered = can_ring_buffer_count(&s_buffer);
    out_stats->dropped = can_ring_buffer_dropped(&s_buffer);
    out_stats->recoveries = s_recoveries;
    out_stats->state = hal_can_get_state();
}
