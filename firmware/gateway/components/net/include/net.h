/**
 * @file net.h
 * @brief WiFi, wall-clock time and MQTT publishing for the gateway.
 *
 * Everything here is asynchronous: net_start() returns at once, the connection comes up in
 * ESP-IDF's own tasks, and net_publish() only hands a message to the MQTT client's outbox, so
 * nothing on the CAN side ever waits for the network. A message that cannot be handed over is
 * counted, never retried here.
 */
#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    const char *ssid;
    const char *password;
    const char *broker_uri;     /**< e.g. "mqtt://192.168.1.10:1883" */
    const char *client_id;      /**< The device id. */
    const char *sntp_server;
    const char *command_topic;  /**< veh/{v}/dev/{d}/cmd/req, subscribed at QoS 1. */
} net_config_t;

/** A processing mode asked for on the command topic. */
typedef enum {
    NET_MODE_REQUEST_NONE = 0,
    NET_MODE_REQUEST_P0,
    NET_MODE_REQUEST_P2,
} net_mode_request_t;

typedef struct {
    uint32_t handed_over;       /**< Messages accepted by the MQTT client's outbox. */
    uint32_t acknowledged;      /**< PUBACKs received from the broker. */
    uint32_t outbox_refused;    /**< The client refused the message (outbox full or not running). */
    uint32_t wifi_disconnects;
    uint32_t mqtt_connects;
    uint32_t mqtt_disconnects;
    uint32_t commands;          /**< Mode commands applied. */
    uint32_t commands_ignored;  /**< Command messages that named no known mode. */
} net_stats_t;

/** Start WiFi, SNTP and the MQTT client. False when the configuration is unusable. */
bool net_start(const net_config_t *config);

/** Connected to the broker. */
bool net_connected(void);

/** The wall clock has been set by SNTP. */
bool net_time_synced(void);

/** Wall-clock time, microseconds since the Unix epoch, UTC. */
int64_t net_unix_now_us(void);

/** Fill @p buf with hardware random bytes. */
void net_random(void *buf, size_t len);

/**
 * The next message sequence number. Monotonic across restarts: numbers are reserved in NVS in
 * blocks, so a restart skips at most one block and flash is written once per block.
 */
uint64_t net_next_sequence(void);

/** Hand a message to the MQTT client at QoS 1. False, and counted, when it was refused. */
bool net_publish(const char *topic, const char *payload, size_t len);

/** The latest mode asked for on the command topic, once; NONE when nothing new arrived. */
net_mode_request_t net_take_mode_request(void);

void net_get_stats(net_stats_t *out);

#ifdef __cplusplus
}
#endif
