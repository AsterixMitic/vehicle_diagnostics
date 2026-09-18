# Transport layer, ISO 15765-2. Pure logic: no driver, no scheduler, no clock.
# Depends on gw_hal only for the vendor-free frame type.
idf_component_register(
    SRCS "isotp.c"
    INCLUDE_DIRS "include"
    REQUIRES gw_hal
)
