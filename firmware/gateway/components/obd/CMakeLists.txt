# OBD-II Mode 01 client: parameter table, decoder, support bitmaps, scheduler.
# Diagnostics layer. Depends on gw_hal only for the vendor-free frame type, as isotp does;
# it calls no driver and performs no I/O, so it builds and tests on a development machine.
idf_component_register(
    SRCS "obd_decode.c" "obd_support.c" "obd_sched.c" "obd_guard.c"
    INCLUDE_DIRS "include"
    REQUIRES gw_hal
)
