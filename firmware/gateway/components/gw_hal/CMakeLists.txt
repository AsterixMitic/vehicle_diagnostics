# Hardware abstraction: the only component that may depend on vendor drivers.
idf_component_register(
    SRCS "hal_can.c"
    INCLUDE_DIRS "include"
    REQUIRES driver esp_timer
)
