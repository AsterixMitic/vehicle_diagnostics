idf_component_register(
    SRCS "main.c" "../../shared/test_can_ring_buffer.c"
    INCLUDE_DIRS "." "../../shared"
    REQUIRES can_link
)
