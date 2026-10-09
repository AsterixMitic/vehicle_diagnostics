# Version-1 message envelope (message-contracts) built from one poll cycle. Application layer,
# framework-free: it formats text into a caller's buffer and performs no I/O, so it builds and
# tests on a development machine. Depends on gw_hal for the frame type and obd for the reading.
idf_component_register(
    SRCS "envelope.c"
    INCLUDE_DIRS "include"
    REQUIRES gw_hal obd
)
