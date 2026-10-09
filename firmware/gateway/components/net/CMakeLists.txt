# Network layer: WiFi station, SNTP, MQTT client at QoS 1, and the persisted message sequence.
# Owns ESP-IDF's network tasks the way can_link owns the receive task, so it calls ESP-IDF
# directly; its public header stays vendor-free.
idf_component_register(
    SRCS "net.c"
    INCLUDE_DIRS "include"
    PRIV_REQUIRES esp_wifi esp_netif esp_event nvs_flash mqtt esp_timer esp_hw_support log
)
