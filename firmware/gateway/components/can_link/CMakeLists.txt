# Link layer. Depends on the hardware abstraction only, never on a vendor driver directly.
idf_component_register(
    SRCS "can_ring_buffer.c" "can_link.c"
    INCLUDE_DIRS "include"
    REQUIRES gw_hal
)
