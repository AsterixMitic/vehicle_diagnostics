/**
 * @file test_can_ring_buffer.c
 * @brief Unit tests for the frame buffer, shared by the host build and the emulated target.
 *
 * One set of assertions, two execution environments: a plain compiler on a development
 * machine, and an emulated ESP32. No test framework, so the host build needs nothing but a
 * C compiler.
 */
#include "test_can_ring_buffer.h"

#include <stdio.h>
#include <string.h>

#include "can_ring_buffer.h"

static int s_failures;
static int s_checks;

#define CHECK(condition, description)                                            \
    do {                                                                         \
        s_checks++;                                                              \
        if (!(condition)) {                                                      \
            s_failures++;                                                        \
            printf("  FAIL %s:%d  %s\n", __func__, __LINE__, (description));     \
        }                                                                        \
    } while (0)

/** Buffer is static in the firmware too, so the test allocates it the same way. */
static can_ring_buffer_t s_rb;

static hal_can_frame_t frame_with(uint32_t id, uint8_t marker)
{
    hal_can_frame_t frame = {0};
    frame.id = id;
    frame.dlc = 8u;
    frame.extended = false;
    frame.timestamp_us = (uint64_t)id * 1000u;
    for (uint8_t i = 0; i < 8u; i++) {
        frame.data[i] = (uint8_t)(marker + i);
    }
    return frame;
}

static void test_starts_empty(void)
{
    can_ring_buffer_init(&s_rb);

    CHECK(can_ring_buffer_is_empty(&s_rb), "a fresh buffer is empty");
    CHECK(!can_ring_buffer_is_full(&s_rb), "a fresh buffer is not full");
    CHECK(can_ring_buffer_count(&s_rb) == 0u, "a fresh buffer holds nothing");
    CHECK(can_ring_buffer_dropped(&s_rb) == 0u, "a fresh buffer has dropped nothing");

    hal_can_frame_t out;
    CHECK(!can_ring_buffer_pop(&s_rb, &out), "popping an empty buffer reports failure");
}

static void test_round_trip_preserves_content(void)
{
    can_ring_buffer_init(&s_rb);
    const hal_can_frame_t in = frame_with(0x7E8u, 0x40u);

    CHECK(can_ring_buffer_push(&s_rb, &in), "pushing into a free buffer succeeds");
    CHECK(can_ring_buffer_count(&s_rb) == 1u, "one pushed frame is one held frame");

    hal_can_frame_t out;
    CHECK(can_ring_buffer_pop(&s_rb, &out), "popping a held frame succeeds");
    CHECK(out.id == in.id, "identifier survives the round trip");
    CHECK(out.dlc == in.dlc, "length survives the round trip");
    CHECK(out.extended == in.extended, "extended flag survives the round trip");
    CHECK(out.timestamp_us == in.timestamp_us, "timestamp survives the round trip");
    CHECK(memcmp(out.data, in.data, sizeof(out.data)) == 0, "payload survives the round trip");
    CHECK(can_ring_buffer_is_empty(&s_rb), "the buffer is empty again afterwards");
}

static void test_no_loss_below_capacity(void)
{
    can_ring_buffer_init(&s_rb);

    for (uint32_t i = 0; i < CAN_RING_BUFFER_USABLE; i++) {
        const hal_can_frame_t frame = frame_with(i, (uint8_t)i);
        CHECK(can_ring_buffer_push(&s_rb, &frame), "every push below capacity succeeds");
    }

    CHECK(can_ring_buffer_count(&s_rb) == CAN_RING_BUFFER_USABLE, "the buffer holds its capacity");
    CHECK(can_ring_buffer_is_full(&s_rb), "at capacity the buffer reports full");
    CHECK(can_ring_buffer_dropped(&s_rb) == 0u, "nothing is dropped below capacity");
}

static void test_overflow_is_counted_not_silent(void)
{
    can_ring_buffer_init(&s_rb);
    for (uint32_t i = 0; i < CAN_RING_BUFFER_USABLE; i++) {
        const hal_can_frame_t frame = frame_with(i, (uint8_t)i);
        (void)can_ring_buffer_push(&s_rb, &frame);
    }

    const hal_can_frame_t overflow = frame_with(0x123u, 0xAAu);
    CHECK(!can_ring_buffer_push(&s_rb, &overflow), "pushing into a full buffer reports failure");
    CHECK(can_ring_buffer_dropped(&s_rb) == 1u, "one rejected frame is one counted drop");
    CHECK(can_ring_buffer_count(&s_rb) == CAN_RING_BUFFER_USABLE, "a rejected frame does not displace a held one");

    for (int i = 0; i < 5; i++) {
        (void)can_ring_buffer_push(&s_rb, &overflow);
    }
    CHECK(can_ring_buffer_dropped(&s_rb) == 6u, "every rejection increments the counter");

    /* Drop-newest, not overwrite-oldest: the oldest frame must still be the one that comes out. */
    hal_can_frame_t out;
    CHECK(can_ring_buffer_pop(&s_rb, &out), "the buffer still yields its contents");
    CHECK(out.id == 0u, "the oldest frame survived the overflow, newest was dropped");
}

static void test_order_is_preserved(void)
{
    can_ring_buffer_init(&s_rb);
    const uint32_t count = 10u;

    for (uint32_t i = 0; i < count; i++) {
        const hal_can_frame_t frame = frame_with(i, (uint8_t)i);
        (void)can_ring_buffer_push(&s_rb, &frame);
    }

    for (uint32_t i = 0; i < count; i++) {
        hal_can_frame_t out;
        CHECK(can_ring_buffer_pop(&s_rb, &out), "each queued frame comes back");
        CHECK(out.id == i, "frames come back in the order they arrived");
    }
}

static void test_survives_wrap_around(void)
{
    can_ring_buffer_init(&s_rb);

    /* Push and pop far more frames than the buffer can hold at once, so the indices wrap
     * several times. The mask arithmetic is what this exercises. */
    const uint32_t total = CAN_RING_BUFFER_CAPACITY * 4u + 7u;
    for (uint32_t i = 0; i < total; i++) {
        const hal_can_frame_t frame = frame_with(i & HAL_CAN_MAX_STD_ID, (uint8_t)i);
        CHECK(can_ring_buffer_push(&s_rb, &frame), "push succeeds while the buffer drains");

        hal_can_frame_t out;
        CHECK(can_ring_buffer_pop(&s_rb, &out), "pop succeeds while the buffer fills");
        CHECK(out.id == (i & HAL_CAN_MAX_STD_ID), "content stays correct across the wrap");
    }

    CHECK(can_ring_buffer_is_empty(&s_rb), "the buffer ends empty");
    CHECK(can_ring_buffer_dropped(&s_rb) == 0u, "draining as fast as filling drops nothing");
}

static void test_partial_drain_then_refill(void)
{
    can_ring_buffer_init(&s_rb);

    for (uint32_t i = 0; i < CAN_RING_BUFFER_USABLE; i++) {
        const hal_can_frame_t frame = frame_with(i, (uint8_t)i);
        (void)can_ring_buffer_push(&s_rb, &frame);
    }

    const uint32_t drained = CAN_RING_BUFFER_USABLE / 2u;
    for (uint32_t i = 0; i < drained; i++) {
        hal_can_frame_t out;
        (void)can_ring_buffer_pop(&s_rb, &out);
    }
    CHECK(can_ring_buffer_count(&s_rb) == CAN_RING_BUFFER_USABLE - drained,
          "draining frees exactly what it removed");

    for (uint32_t i = 0; i < drained; i++) {
        const hal_can_frame_t frame = frame_with(0x200u + i, (uint8_t)i);
        CHECK(can_ring_buffer_push(&s_rb, &frame), "freed slots accept new frames");
    }
    CHECK(can_ring_buffer_dropped(&s_rb) == 0u, "refilling freed space drops nothing");
}

static void test_rejects_null_arguments(void)
{
    can_ring_buffer_init(&s_rb);
    const hal_can_frame_t frame = frame_with(0x100u, 0x11u);
    hal_can_frame_t out;

    CHECK(!can_ring_buffer_push(NULL, &frame), "pushing to no buffer fails rather than crashes");
    CHECK(!can_ring_buffer_push(&s_rb, NULL), "pushing no frame fails rather than crashes");
    CHECK(!can_ring_buffer_pop(NULL, &out), "popping from no buffer fails rather than crashes");
    CHECK(!can_ring_buffer_pop(&s_rb, NULL), "popping into nothing fails rather than crashes");
    CHECK(can_ring_buffer_count(NULL) == 0u, "counting no buffer is zero, not a crash");
}

int can_ring_buffer_tests_run(void)
{
    s_failures = 0;
    s_checks = 0;

    printf("ring buffer: capacity %u slots, %u usable\n",
           (unsigned)CAN_RING_BUFFER_CAPACITY, (unsigned)CAN_RING_BUFFER_USABLE);

    test_starts_empty();
    test_round_trip_preserves_content();
    test_no_loss_below_capacity();
    test_overflow_is_counted_not_silent();
    test_order_is_preserved();
    test_survives_wrap_around();
    test_partial_drain_then_refill();
    test_rejects_null_arguments();

    printf("%d checks, %d failures\n", s_checks, s_failures);
    return s_failures;
}
