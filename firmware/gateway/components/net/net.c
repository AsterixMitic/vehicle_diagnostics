/**
 * @file net.c
 * @brief WiFi station, SNTP and MQTT client, see net.h.
 */
#include "net.h"

#include <stdatomic.h>
#include <string.h>
#include <sys/time.h>
#include <time.h>

#include "esp_event.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_netif_sntp.h"
#include "esp_random.h"
#include "esp_wifi.h"
#include "mqtt_client.h"
#include "nvs.h"
#include "nvs_flash.h"

static const char *TAG = "net";

#define SEQUENCE_BLOCK 64u
#define MIN_VALID_UNIX_S 1735689600  /* 2025-01-01: anything earlier is an unset clock */
#define COMMAND_MAX 128u

static esp_mqtt_client_handle_t s_client;
static char s_command_topic[96];
static atomic_bool s_connected;
static atomic_int s_mode_request;
static nvs_handle_t s_nvs;
static uint64_t s_sequence_next;
static uint64_t s_sequence_reserved;  /* first number not yet reserved in NVS */

static atomic_uint s_handed_over, s_acknowledged, s_outbox_refused, s_wifi_disconnects, s_mqtt_connects,
    s_mqtt_disconnects, s_commands, s_commands_ignored;

static void on_wifi(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    (void)arg;
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        const wifi_event_sta_disconnected_t *event = (const wifi_event_sta_disconnected_t *)data;
        ESP_LOGW(TAG, "WiFi down, reason %u, access point at %d dBm", (unsigned)event->reason, (int)event->rssi);
        atomic_fetch_add(&s_wifi_disconnects, 1u);
        esp_wifi_connect();  /* keep trying; the MQTT client reconnects on its own once IP is back */
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        const ip_event_got_ip_t *event = (const ip_event_got_ip_t *)data;
        ESP_LOGI(TAG, "WiFi up, address " IPSTR, IP2STR(&event->ip_info.ip));
    }
}

static void apply_command(const char *text)
{
    /* The command is {"processingMode":"P0"} or "P2"; anything else is counted and ignored. */
    const char *key = strstr(text, "\"processingMode\"");
    const char *p0 = key ? strstr(key, "\"P0\"") : NULL;
    const char *p2 = key ? strstr(key, "\"P2\"") : NULL;
    if (p0 && !p2) {
        atomic_store(&s_mode_request, NET_MODE_REQUEST_P0);
        atomic_fetch_add(&s_commands, 1u);
    } else if (p2 && !p0) {
        atomic_store(&s_mode_request, NET_MODE_REQUEST_P2);
        atomic_fetch_add(&s_commands, 1u);
    } else {
        atomic_fetch_add(&s_commands_ignored, 1u);
    }
}

static void on_mqtt(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    (void)arg;
    (void)base;
    const esp_mqtt_event_handle_t event = (esp_mqtt_event_handle_t)data;
    switch ((esp_mqtt_event_id_t)id) {
    case MQTT_EVENT_CONNECTED:
        atomic_store(&s_connected, true);
        atomic_fetch_add(&s_mqtt_connects, 1u);
        esp_mqtt_client_subscribe(s_client, s_command_topic, 1);
        ESP_LOGI(TAG, "MQTT connected, commands on %s", s_command_topic);
        break;
    case MQTT_EVENT_DISCONNECTED:
        if (atomic_exchange(&s_connected, false)) {
            atomic_fetch_add(&s_mqtt_disconnects, 1u);
        }
        break;
    case MQTT_EVENT_PUBLISHED:
        atomic_fetch_add(&s_acknowledged, 1u);
        break;
    case MQTT_EVENT_DATA:
        if (event->current_data_offset == 0 && event->topic_len == (int)strlen(s_command_topic) &&
            memcmp(event->topic, s_command_topic, (size_t)event->topic_len) == 0) {
            char text[COMMAND_MAX + 1u];
            const size_t n = event->data_len < (int)COMMAND_MAX ? (size_t)event->data_len : COMMAND_MAX;
            memcpy(text, event->data, n);
            text[n] = '\0';
            apply_command(text);
        }
        break;
    default:
        break;
    }
}

static void sequence_init(void)
{
    uint64_t stored = 0u;
    if (nvs_open("gateway", NVS_READWRITE, &s_nvs) == ESP_OK) {
        (void)nvs_get_u64(s_nvs, "seq_next", &stored);  /* absent on first boot: start at 0 */
    }
    s_sequence_next = stored;
    s_sequence_reserved = stored;
}

uint64_t net_next_sequence(void)
{
    if (s_sequence_next >= s_sequence_reserved) {
        s_sequence_reserved = s_sequence_next + SEQUENCE_BLOCK;
        if (nvs_set_u64(s_nvs, "seq_next", s_sequence_reserved) != ESP_OK || nvs_commit(s_nvs) != ESP_OK) {
            ESP_LOGW(TAG, "could not reserve sequence numbers in NVS; a restart may repeat them");
        }
    }
    return s_sequence_next++;
}

bool net_start(const net_config_t *config)
{
    if (config == NULL || config->ssid == NULL || config->ssid[0] == '\0') {
        return false;
    }
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        nvs_flash_erase();
        err = nvs_flash_init();
    }
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "NVS init failed: %s", esp_err_to_name(err));
        return false;
    }
    sequence_init();

    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_sta();
    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&init));
    ESP_ERROR_CHECK(esp_event_handler_instance_register(WIFI_EVENT, ESP_EVENT_ANY_ID, on_wifi, NULL, NULL));
    ESP_ERROR_CHECK(esp_event_handler_instance_register(IP_EVENT, IP_EVENT_STA_GOT_IP, on_wifi, NULL, NULL));
    wifi_config_t wifi = {0};
    strncpy((char *)wifi.sta.ssid, config->ssid, sizeof(wifi.sta.ssid) - 1u);
    strncpy((char *)wifi.sta.password, config->password, sizeof(wifi.sta.password) - 1u);
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &wifi));
    ESP_ERROR_CHECK(esp_wifi_start());

    esp_sntp_config_t sntp = ESP_NETIF_SNTP_DEFAULT_CONFIG(config->sntp_server);
    ESP_ERROR_CHECK(esp_netif_sntp_init(&sntp));

    strncpy(s_command_topic, config->command_topic, sizeof(s_command_topic) - 1u);
    const esp_mqtt_client_config_t mqtt = {
        .broker.address.uri = config->broker_uri,
        .credentials.client_id = config->client_id,
    };
    s_client = esp_mqtt_client_init(&mqtt);
    if (s_client == NULL) {
        ESP_LOGE(TAG, "MQTT client could not be created; check the broker URI");
        return false;
    }
    ESP_ERROR_CHECK(esp_mqtt_client_register_event(s_client, ESP_EVENT_ANY_ID, on_mqtt, NULL));
    ESP_ERROR_CHECK(esp_mqtt_client_start(s_client));
    ESP_LOGI(TAG, "connecting to WiFi '%s', broker %s, sequence continues at %llu", config->ssid,
             config->broker_uri, (unsigned long long)s_sequence_next);
    return true;
}

bool net_connected(void)
{
    return atomic_load(&s_connected);
}

bool net_time_synced(void)
{
    return time(NULL) >= MIN_VALID_UNIX_S;
}

int64_t net_unix_now_us(void)
{
    struct timeval now;
    gettimeofday(&now, NULL);
    return (int64_t)now.tv_sec * 1000000 + now.tv_usec;
}

void net_random(void *buf, size_t len)
{
    esp_fill_random(buf, len);
}

bool net_publish(const char *topic, const char *payload, size_t len)
{
    /* enqueue, not publish: the client's own task sends it, so this never waits on the network. */
    if (s_client == NULL || esp_mqtt_client_enqueue(s_client, topic, payload, (int)len, 1, 0, true) < 0) {
        atomic_fetch_add(&s_outbox_refused, 1u);
        return false;
    }
    atomic_fetch_add(&s_handed_over, 1u);
    return true;
}

net_mode_request_t net_take_mode_request(void)
{
    return (net_mode_request_t)atomic_exchange(&s_mode_request, NET_MODE_REQUEST_NONE);
}

void net_get_stats(net_stats_t *out)
{
    out->handed_over = atomic_load(&s_handed_over);
    out->acknowledged = atomic_load(&s_acknowledged);
    out->outbox_refused = atomic_load(&s_outbox_refused);
    out->wifi_disconnects = atomic_load(&s_wifi_disconnects);
    out->mqtt_connects = atomic_load(&s_mqtt_connects);
    out->mqtt_disconnects = atomic_load(&s_mqtt_disconnects);
    out->commands = atomic_load(&s_commands);
    out->commands_ignored = atomic_load(&s_commands_ignored);
}
