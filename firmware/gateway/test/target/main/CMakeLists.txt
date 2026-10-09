idf_component_register(
    SRCS "main.c"
         "fixture_target.c"
         "../../shared/test_can_ring_buffer.c"
         "../../shared/test_isotp.c"
         "../../shared/test_obd.c"
         "../../shared/test_obd_sched.c"
         "../../shared/test_envelope.c"
    INCLUDE_DIRS "." "../../shared"
    REQUIRES can_link isotp obd envelope
    # The backend's decoder fixture, so both decoders are checked against one copy.
    EMBED_TXTFILES "../../../../../experiments/captures/synthetic-obd-mode01.log"
                   "../../../../../experiments/captures/synthetic-obd-mode01.expected.json"
)
