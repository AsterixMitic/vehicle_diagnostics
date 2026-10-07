/**
 * @file obd_support.c
 * @brief Mode 01 support bitmaps (PIDs 0x00, 0x20, 0x40).
 *
 * The answer to support PID r is four bytes A B C D. Bit 7 of A declares PID r+1, bit 0 of D
 * declares PID r+0x20, which is also the next support PID, so the last bit of each range says
 * whether asking the next range makes sense.
 */
#include "obd.h"

static int range_index(uint8_t range_pid)
{
    switch (range_pid) {
    case 0x00u:
        return 0;
    case 0x20u:
        return 1;
    case 0x40u:
        return 2;
    default:
        return -1;
    }
}

void obd_support_init(obd_support_t *support)
{
    if (support == NULL) {
        return;
    }
    for (int i = 0; i < 3; i++) {
        support->bitmap[i] = 0u;
        support->known[i] = false;
    }
}

bool obd_is_support_pid(uint8_t pid)
{
    return range_index(pid) >= 0;
}

bool obd_support_apply(obd_support_t *support, uint8_t range_pid, const uint8_t data[4])
{
    const int index = range_index(range_pid);
    if (support == NULL || data == NULL || index < 0) {
        return false;
    }
    support->bitmap[index] = ((uint32_t)data[0] << 24) | ((uint32_t)data[1] << 16) |
                             ((uint32_t)data[2] << 8) | (uint32_t)data[3];
    support->known[index] = true;
    return true;
}

bool obd_support_has(const obd_support_t *support, uint8_t pid)
{
    if (support == NULL || pid == 0u || pid > 0x60u) {
        return false;
    }
    const int index = (pid - 1) / 0x20;
    if (!support->known[index]) {
        return false;
    }
    const unsigned bit = (unsigned)(pid - 1 - index * 0x20);  /* 0 = bit 7 of A */
    return (support->bitmap[index] & (UINT32_C(1) << (31u - bit))) != 0u;
}

bool obd_support_declares_next(const obd_support_t *support, uint8_t range_pid)
{
    const int index = range_index(range_pid);
    if (support == NULL || index < 0 || !support->known[index]) {
        return false;
    }
    return (support->bitmap[index] & UINT32_C(1)) != 0u;
}

bool obd_parse_support_response(const hal_can_frame_t *frame, uint8_t *range_pid, uint8_t data[4])
{
    if (frame == NULL || range_pid == NULL || data == NULL) {
        return false;
    }
    if (!obd_is_response_id(frame->id, frame->extended) || frame->dlc < 7u) {
        return false;
    }
    /* Single frame of at least six bytes: 0x41, the support PID, A, B, C, D. */
    const uint8_t pci = frame->data[0];
    if ((pci >> 4) != 0u || (pci & 0x0Fu) < 6u || (pci & 0x0Fu) > 7u) {
        return false;
    }
    if (frame->data[1] != OBD_POSITIVE_CURRENT_DATA || !obd_is_support_pid(frame->data[2])) {
        return false;
    }
    *range_pid = frame->data[2];
    for (int i = 0; i < 4; i++) {
        data[i] = frame->data[3 + i];
    }
    return true;
}
