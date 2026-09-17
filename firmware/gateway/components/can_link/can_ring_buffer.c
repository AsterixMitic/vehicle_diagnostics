#include "can_ring_buffer.h"

#include <string.h>

/** Power-of-two capacity, so the wrap is a mask. */
#define RB_MASK (CAN_RING_BUFFER_CAPACITY - 1u)

void can_ring_buffer_init(can_ring_buffer_t *rb)
{
    if (rb == NULL) {
        return;
    }
    atomic_store_explicit(&rb->head, 0u, memory_order_relaxed);
    atomic_store_explicit(&rb->tail, 0u, memory_order_relaxed);
    atomic_store_explicit(&rb->dropped, 0u, memory_order_relaxed);
}

bool can_ring_buffer_push(can_ring_buffer_t *rb, const hal_can_frame_t *frame)
{
    if (rb == NULL || frame == NULL) {
        return false;
    }

    /* Producer owns head, so a relaxed load of it is enough. Tail needs acquire so the slot
     * the consumer just released is visible as free before we reuse it. */
    const uint32_t head = atomic_load_explicit(&rb->head, memory_order_relaxed);
    const uint32_t next = (head + 1u) & RB_MASK;
    const uint32_t tail = atomic_load_explicit(&rb->tail, memory_order_acquire);

    if (next == tail) {
        /* Full: reject the newest and count it. Loss is never silent. */
        atomic_fetch_add_explicit(&rb->dropped, 1u, memory_order_relaxed);
        return false;
    }

    rb->slots[head] = *frame;

    /* Release: the slot write above must be visible before the consumer sees the new head. */
    atomic_store_explicit(&rb->head, next, memory_order_release);
    return true;
}

bool can_ring_buffer_pop(can_ring_buffer_t *rb, hal_can_frame_t *out_frame)
{
    if (rb == NULL || out_frame == NULL) {
        return false;
    }

    const uint32_t tail = atomic_load_explicit(&rb->tail, memory_order_relaxed);
    const uint32_t head = atomic_load_explicit(&rb->head, memory_order_acquire);

    if (tail == head) {
        return false; /* Empty. */
    }

    *out_frame = rb->slots[tail];

    /* Release: the slot read above must complete before the producer may reuse it. */
    atomic_store_explicit(&rb->tail, (tail + 1u) & RB_MASK, memory_order_release);
    return true;
}

uint32_t can_ring_buffer_count(const can_ring_buffer_t *rb)
{
    if (rb == NULL) {
        return 0u;
    }
    const uint32_t head = atomic_load_explicit(&rb->head, memory_order_acquire);
    const uint32_t tail = atomic_load_explicit(&rb->tail, memory_order_acquire);
    return (head - tail) & RB_MASK;
}

uint32_t can_ring_buffer_dropped(const can_ring_buffer_t *rb)
{
    return rb == NULL ? 0u : atomic_load_explicit(&rb->dropped, memory_order_relaxed);
}

bool can_ring_buffer_is_empty(const can_ring_buffer_t *rb)
{
    return can_ring_buffer_count(rb) == 0u;
}

bool can_ring_buffer_is_full(const can_ring_buffer_t *rb)
{
    return can_ring_buffer_count(rb) == CAN_RING_BUFFER_USABLE;
}
