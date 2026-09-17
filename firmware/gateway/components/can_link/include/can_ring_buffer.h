/**
 * @file can_ring_buffer.h
 * @brief Bounded lock-free frame buffer between the receive task and its consumer.
 *
 * Single producer, single consumer. The producer is the receive task; exactly one consumer
 * may pop. Storage is static and the capacity is a compile-time constant, so nothing on the
 * receive path allocates and timing does not depend on heap state (architecture.md §12.1).
 *
 * **Overflow policy: the newest frame is rejected and counted.** Overwriting the oldest would
 * keep fresher data but leave the drop counter unable to say which frames are missing, and a
 * capture destined for replay is worth more as a contiguous prefix than as a fresher window
 * with holes. A rejected frame always increments the drop counter; loss is never silent.
 *
 * Depends only on the vendor-free frame type, so this file compiles and its tests run on a
 * development machine with no ESP-IDF installed.
 */
#pragma once

#include <stdatomic.h>
#include <stdbool.h>
#include <stdint.h>

#include "hal_can.h"

#ifdef __cplusplus
extern "C" {
#endif

/** Slot count. Must be a power of two so the wrap is a mask rather than a division. */
#ifndef CAN_RING_BUFFER_CAPACITY
#define CAN_RING_BUFFER_CAPACITY 128u
#endif

_Static_assert(CAN_RING_BUFFER_CAPACITY >= 2u, "ring buffer needs at least two slots");
_Static_assert((CAN_RING_BUFFER_CAPACITY & (CAN_RING_BUFFER_CAPACITY - 1u)) == 0u,
               "CAN_RING_BUFFER_CAPACITY must be a power of two");

/**
 * Frames the buffer can hold at once. One slot is reserved to tell "full" from "empty"
 * without a separate count, so this is one less than the slot count.
 */
#define CAN_RING_BUFFER_USABLE (CAN_RING_BUFFER_CAPACITY - 1u)

typedef struct {
    hal_can_frame_t slots[CAN_RING_BUFFER_CAPACITY];
    _Atomic uint32_t head;    /**< Next slot to write. Producer only. */
    _Atomic uint32_t tail;    /**< Next slot to read. Consumer only. */
    _Atomic uint32_t dropped; /**< Frames rejected because the buffer was full. */
} can_ring_buffer_t;

/** Reset to empty and zero the drop counter. Call before any producer or consumer runs. */
void can_ring_buffer_init(can_ring_buffer_t *rb);

/**
 * Append one frame. Producer side only.
 * @return true when stored; false when the buffer was full, in which case the frame is
 *         discarded and the drop counter has been incremented.
 */
bool can_ring_buffer_push(can_ring_buffer_t *rb, const hal_can_frame_t *frame);

/**
 * Take the oldest frame. Consumer side only.
 * @return true when a frame was written to @p out_frame; false when the buffer was empty.
 */
bool can_ring_buffer_pop(can_ring_buffer_t *rb, hal_can_frame_t *out_frame);

/** Frames currently held. Safe to call from either side; a snapshot, not a lock. */
uint32_t can_ring_buffer_count(const can_ring_buffer_t *rb);

/** Total frames rejected for lack of space since the last init. */
uint32_t can_ring_buffer_dropped(const can_ring_buffer_t *rb);

bool can_ring_buffer_is_empty(const can_ring_buffer_t *rb);

bool can_ring_buffer_is_full(const can_ring_buffer_t *rb);

#ifdef __cplusplus
}
#endif
