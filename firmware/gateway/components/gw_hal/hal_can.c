/**
 * @file hal_can.c
 * @brief TWAI-backed implementation of the vendor-free CAN interface.
 *
 * This is the only translation unit in the firmware permitted to include vendor headers.
 * Keeping the boundary here means a future move to another controller or to the newer TWAI
 * interface touches one file (design D2).
 */
#include "hal_can.h"

#include "driver/twai.h"
#include "esp_err.h"
#include "esp_log.h"
#include "esp_timer.h"

static const char *TAG = "hal_can";

static bool s_installed;
static hal_can_counters_t s_counters;

/** Bit timing for the rates this project uses. Classic CAN only. */
static bool timing_for(uint32_t bitrate_kbps, twai_timing_config_t *out)
{
    switch (bitrate_kbps) {
    case 500u: {
        twai_timing_config_t t = TWAI_TIMING_CONFIG_500KBITS();
        *out = t;
        return true;
    }
    case 250u: {
        twai_timing_config_t t = TWAI_TIMING_CONFIG_250KBITS();
        *out = t;
        return true;
    }
    case 125u: {
        twai_timing_config_t t = TWAI_TIMING_CONFIG_125KBITS();
        *out = t;
        return true;
    }
    default:
        return false;
    }
}

static void filter_for(const hal_can_config_t *config, twai_filter_config_t *out)
{
    if (config->filter_mode == HAL_CAN_FILTER_ACCEPT_ALL) {
        twai_filter_config_t f = TWAI_FILTER_CONFIG_ACCEPT_ALL();
        *out = f;
        return;
    }

    /*
     * The acceptance filter compares the top bits of the incoming identifier. For a standard
     * frame the controller places the 11-bit id in bits 31..21, and a set mask bit means
     * "don't care", which is the inverse of the caller's mask.
     */
    twai_filter_config_t f = {
        .acceptance_code = config->filter_id << 21,
        .acceptance_mask = ~(config->filter_mask << 21),
        .single_filter = true,
    };
    *out = f;
}

hal_can_err_t hal_can_init(const hal_can_config_t *config)
{
    if (config == NULL) {
        return HAL_CAN_ERR_INVALID_ARG;
    }
    if (s_installed) {
        return HAL_CAN_ERR_INVALID_STATE;
    }

    twai_timing_config_t timing;
    if (!timing_for(config->bitrate_kbps, &timing)) {
        ESP_LOGE(TAG, "unsupported bitrate %u kbps", (unsigned)config->bitrate_kbps);
        return HAL_CAN_ERR_INVALID_ARG;
    }

    twai_filter_config_t filter;
    filter_for(config, &filter);

    twai_general_config_t general = TWAI_GENERAL_CONFIG_DEFAULT(
        (gpio_num_t)config->tx_gpio, (gpio_num_t)config->rx_gpio, TWAI_MODE_NORMAL);
    /* A deeper driver queue absorbs bursts before our ring buffer ever sees them. */
    general.rx_queue_len = 32;
    general.tx_queue_len = 16;

    esp_err_t err = twai_driver_install(&general, &timing, &filter);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "twai_driver_install failed: %s", esp_err_to_name(err));
        return HAL_CAN_ERR_FAILED;
    }

    s_installed = true;
    s_counters = (hal_can_counters_t){0};
    ESP_LOGI(TAG, "installed: %u kbps, tx=GPIO%d rx=GPIO%d, filter=%s",
             (unsigned)config->bitrate_kbps, config->tx_gpio, config->rx_gpio,
             config->filter_mode == HAL_CAN_FILTER_ACCEPT_ALL ? "accept-all" : "mask");
    return HAL_CAN_OK;
}

hal_can_err_t hal_can_start(void)
{
    if (!s_installed) {
        return HAL_CAN_ERR_INVALID_STATE;
    }
    return twai_start() == ESP_OK ? HAL_CAN_OK : HAL_CAN_ERR_FAILED;
}

hal_can_err_t hal_can_stop(void)
{
    if (!s_installed) {
        return HAL_CAN_ERR_INVALID_STATE;
    }
    (void)twai_stop();
    (void)twai_driver_uninstall();
    s_installed = false;
    return HAL_CAN_OK;
}

hal_can_err_t hal_can_receive(hal_can_frame_t *out_frame, uint32_t timeout_ms)
{
    if (out_frame == NULL) {
        return HAL_CAN_ERR_INVALID_ARG;
    }
    if (!s_installed) {
        return HAL_CAN_ERR_INVALID_STATE;
    }

    twai_message_t message;
    esp_err_t err = twai_receive(&message, pdMS_TO_TICKS(timeout_ms));
    if (err == ESP_ERR_TIMEOUT) {
        return HAL_CAN_ERR_TIMEOUT;
    }
    if (err != ESP_OK) {
        s_counters.bus_errors++;
        return HAL_CAN_ERR_FAILED;
    }

    /* Stamp immediately: any later and the number stops describing the bus (§18 jitter). */
    out_frame->timestamp_us = (uint64_t)esp_timer_get_time();
    out_frame->id = message.identifier;
    out_frame->extended = message.extd != 0;
    out_frame->dlc = message.data_length_code > HAL_CAN_MAX_DLC
                         ? (uint8_t)HAL_CAN_MAX_DLC
                         : (uint8_t)message.data_length_code;
    for (uint8_t i = 0; i < out_frame->dlc; i++) {
        out_frame->data[i] = message.data[i];
    }
    for (uint8_t i = out_frame->dlc; i < HAL_CAN_MAX_DLC; i++) {
        out_frame->data[i] = 0u;
    }

    s_counters.rx_frames++;
    return HAL_CAN_OK;
}

hal_can_err_t hal_can_transmit(const hal_can_frame_t *frame, uint32_t timeout_ms)
{
    if (frame == NULL || frame->dlc > HAL_CAN_MAX_DLC) {
        return HAL_CAN_ERR_INVALID_ARG;
    }
    if (!s_installed) {
        return HAL_CAN_ERR_INVALID_STATE;
    }
    if (frame->id > (frame->extended ? HAL_CAN_MAX_EXT_ID : HAL_CAN_MAX_STD_ID)) {
        return HAL_CAN_ERR_INVALID_ARG;
    }

    twai_message_t message = {0};
    message.identifier = frame->id;
    message.extd = frame->extended ? 1u : 0u;
    message.data_length_code = frame->dlc;
    for (uint8_t i = 0; i < frame->dlc; i++) {
        message.data[i] = frame->data[i];
    }

    esp_err_t err = twai_transmit(&message, pdMS_TO_TICKS(timeout_ms));
    if (err == ESP_ERR_TIMEOUT) {
        return HAL_CAN_ERR_TIMEOUT;
    }
    if (err != ESP_OK) {
        s_counters.bus_errors++;
        return HAL_CAN_ERR_FAILED;
    }

    s_counters.tx_frames++;
    return HAL_CAN_OK;
}

hal_can_err_t hal_can_get_counters(hal_can_counters_t *out_counters)
{
    if (out_counters == NULL) {
        return HAL_CAN_ERR_INVALID_ARG;
    }

    twai_status_info_t status;
    if (s_installed && twai_get_status_info(&status) == ESP_OK) {
        s_counters.rx_missed = status.rx_missed_count;
        s_counters.bus_errors = status.bus_error_count;
    }
    *out_counters = s_counters;
    return HAL_CAN_OK;
}

hal_can_state_t hal_can_get_state(void)
{
    twai_status_info_t status;
    if (!s_installed || twai_get_status_info(&status) != ESP_OK) {
        return HAL_CAN_STATE_STOPPED;
    }

    switch (status.state) {
    case TWAI_STATE_RUNNING:
        return HAL_CAN_STATE_RUNNING;
    case TWAI_STATE_BUS_OFF:
        return HAL_CAN_STATE_BUS_OFF;
    case TWAI_STATE_RECOVERING:
        return HAL_CAN_STATE_RECOVERING;
    case TWAI_STATE_STOPPED:
    default:
        return HAL_CAN_STATE_STOPPED;
    }
}

hal_can_err_t hal_can_recover(void)
{
    if (!s_installed) {
        return HAL_CAN_ERR_INVALID_STATE;
    }
    esp_err_t err = twai_initiate_recovery();
    if (err != ESP_OK) {
        return HAL_CAN_ERR_FAILED;
    }
    ESP_LOGW(TAG, "bus-off detected, recovery initiated");
    return HAL_CAN_OK;
}

uint64_t hal_can_now_us(void)
{
    return (uint64_t)esp_timer_get_time();
}
