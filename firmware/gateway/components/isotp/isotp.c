#include "isotp.h"

#include <string.h>

/* ------------------------------------------------------------------ helpers ------- */

static void outcome_rx_clear(isotp_rx_outcome_t *out) { memset(out, 0, sizeof(*out)); }

static void outcome_tx_clear(isotp_tx_outcome_t *out) { memset(out, 0, sizeof(*out)); }

/** Build a frame padded to full length; real ECUs commonly expect eight bytes. */
static void frame_build(hal_can_frame_t *frame, uint32_t id, const uint8_t *payload, uint8_t len)
{
    memset(frame, 0, sizeof(*frame));
    frame->id = id;
    frame->extended = false;
    frame->dlc = (uint8_t)HAL_CAN_MAX_DLC;
    for (uint8_t i = 0; i < len; i++) {
        frame->data[i] = payload[i];
    }
    for (uint8_t i = len; i < (uint8_t)HAL_CAN_MAX_DLC; i++) {
        frame->data[i] = (uint8_t)ISOTP_PADDING_BYTE;
    }
}

/** True once at least @p span microseconds have passed since @p since. */
static bool elapsed(uint64_t now_us, uint64_t since_us, uint32_t span_us)
{
    if (now_us < since_us) {
        return false; /* Clock went backwards; never abandon on that alone. */
    }
    return (now_us - since_us) >= (uint64_t)span_us;
}

const char *isotp_err_tag(isotp_err_t error)
{
    switch (error) {
    case ISOTP_OK:
        return "ok";
    case ISOTP_ERR_INVALID_ARG:
        return "invalid_arg";
    case ISOTP_ERR_BUSY:
        return "busy";
    case ISOTP_ERR_MALFORMED:
        return "malformed";
    case ISOTP_ERR_UNEXPECTED_FRAME:
        return "unexpected_frame";
    case ISOTP_ERR_WRONG_SEQUENCE:
        return "wrong_sequence";
    case ISOTP_ERR_OVERFLOW:
        return "overflow";
    case ISOTP_ERR_UNSUPPORTED:
        return "unsupported";
    case ISOTP_ERR_TIMEOUT_N_CR:
        return "timeout_n_cr";
    case ISOTP_ERR_TIMEOUT_N_BS:
        return "timeout_n_bs";
    default:
        return "unknown";
    }
}

uint32_t isotp_decode_stmin_us(uint8_t raw)
{
    if (raw <= 0x7Fu) {
        return (uint32_t)raw * 1000u;
    }
    if (raw >= 0xF1u && raw <= 0xF9u) {
        return (uint32_t)(raw - 0xF0u) * 100u;
    }
    /* Reserved. Treating it as zero would flood a peer that asked to be slowed down. */
    return (uint32_t)ISOTP_STMIN_MAX_US;
}

/* ----------------------------------------------------------------- receiver ------- */

void isotp_rx_init(isotp_rx_t *rx, uint32_t rx_can_id, uint32_t fc_can_id, uint8_t block_size,
                   uint8_t st_min_raw)
{
    if (rx == NULL) {
        return;
    }
    memset(rx, 0, sizeof(*rx));
    rx->rx_can_id = rx_can_id;
    rx->fc_can_id = fc_can_id;
    rx->block_size = block_size;
    rx->st_min_raw = st_min_raw;
    rx->state = ISOTP_RX_IDLE;
}

void isotp_rx_reset(isotp_rx_t *rx)
{
    if (rx == NULL) {
        return;
    }
    rx->state = ISOTP_RX_IDLE;
    rx->expected_len = 0u;
    rx->received_len = 0u;
    rx->next_sn = 0u;
    rx->block_remaining = 0u;
}

bool isotp_rx_is_idle(const isotp_rx_t *rx)
{
    return rx == NULL || rx->state == ISOTP_RX_IDLE;
}

static void rx_emit_fc(const isotp_rx_t *rx, isotp_flow_status_t status, isotp_rx_outcome_t *out)
{
    const uint8_t payload[3] = {
        (uint8_t)(((uint8_t)ISOTP_PCI_FLOW_CONTROL << 4) | (uint8_t)status),
        rx->block_size,
        rx->st_min_raw,
    };
    frame_build(&out->frame, rx->fc_can_id, payload, 3u);
    out->send_frame = true;
}

/** Abandon the transfer and report. Leaves anything already placed in @p out intact. */
static isotp_err_t rx_fail(isotp_rx_t *rx, isotp_err_t error, isotp_rx_outcome_t *out)
{
    rx->errors++;
    isotp_rx_reset(rx);
    out->error = true;
    out->error_code = error;
    return error;
}

static isotp_err_t rx_on_single(isotp_rx_t *rx, const hal_can_frame_t *frame, uint64_t now_us,
                                isotp_rx_outcome_t *out)
{
    const uint8_t sf_dl = (uint8_t)(frame->data[0] & 0x0Fu);

    /* Length zero is the CAN FD escape, which this layer does not implement, and on classic
     * CAN it declares no payload at all. Either way there is nothing to deliver. */
    if (sf_dl == 0u || sf_dl > (uint8_t)ISOTP_SF_MAX_PAYLOAD) {
        return rx_fail(rx, ISOTP_ERR_MALFORMED, out);
    }
    if (frame->dlc < (uint8_t)(1u + sf_dl)) {
        return rx_fail(rx, ISOTP_ERR_MALFORMED, out);
    }

    /* A single frame supersedes any transfer in progress. */
    isotp_rx_reset(rx);
    memcpy(rx->buffer, &frame->data[1], sf_dl);
    rx->expected_len = sf_dl;
    rx->received_len = sf_dl;
    rx->last_frame_us = now_us;
    rx->messages_completed++;

    out->message_complete = true;
    out->message = rx->buffer;
    out->message_len = sf_dl;
    return ISOTP_OK;
}

static isotp_err_t rx_on_first(isotp_rx_t *rx, const hal_can_frame_t *frame, uint64_t now_us,
                               isotp_rx_outcome_t *out)
{
    if (frame->dlc < (uint8_t)HAL_CAN_MAX_DLC) {
        return rx_fail(rx, ISOTP_ERR_MALFORMED, out);
    }

    const uint16_t ff_dl =
        (uint16_t)(((uint16_t)(frame->data[0] & 0x0Fu) << 8) | (uint16_t)frame->data[1]);

    if (ff_dl == 0u) {
        /* The 2016 escape sequence for lengths above 4095. Out of scope; refuse rather than
         * misparse the four bytes that follow as payload. */
        return rx_fail(rx, ISOTP_ERR_UNSUPPORTED, out);
    }
    if (ff_dl <= (uint16_t)ISOTP_SF_MAX_PAYLOAD) {
        return rx_fail(rx, ISOTP_ERR_MALFORMED, out); /* Should have been a single frame. */
    }

    if (ff_dl > (uint16_t)ISOTP_RX_BUFFER_SIZE) {
        /* Refusing is the specified behaviour, so the memory bound and the protocol agree. */
        isotp_rx_reset(rx);
        rx_emit_fc(rx, ISOTP_FS_OVERFLOW, out);
        rx->errors++;
        out->error = true;
        out->error_code = ISOTP_ERR_OVERFLOW;
        return ISOTP_ERR_OVERFLOW;
    }

    isotp_rx_reset(rx);
    memcpy(rx->buffer, &frame->data[2], (size_t)ISOTP_FF_PAYLOAD);
    rx->expected_len = ff_dl;
    rx->received_len = (uint16_t)ISOTP_FF_PAYLOAD;
    rx->next_sn = 1u;
    rx->block_remaining = (rx->block_size == 0u) ? 0u : (uint16_t)rx->block_size;
    rx->state = ISOTP_RX_ASSEMBLING;
    rx->last_frame_us = now_us;

    rx_emit_fc(rx, ISOTP_FS_CONTINUE, out);
    return ISOTP_OK;
}

static isotp_err_t rx_on_consecutive(isotp_rx_t *rx, const hal_can_frame_t *frame, uint64_t now_us,
                                     isotp_rx_outcome_t *out)
{
    if (rx->state != ISOTP_RX_ASSEMBLING) {
        return rx_fail(rx, ISOTP_ERR_UNEXPECTED_FRAME, out);
    }

    const uint8_t sn = (uint8_t)(frame->data[0] & 0x0Fu);
    if (sn != rx->next_sn) {
        return rx_fail(rx, ISOTP_ERR_WRONG_SEQUENCE, out);
    }

    const uint16_t remaining = (uint16_t)(rx->expected_len - rx->received_len);
    uint16_t available = (uint16_t)(frame->dlc - 1u);
    if (available > (uint16_t)ISOTP_CF_MAX_PAYLOAD) {
        available = (uint16_t)ISOTP_CF_MAX_PAYLOAD;
    }
    const uint16_t take = (remaining < available) ? remaining : available;
    if (take == 0u) {
        return rx_fail(rx, ISOTP_ERR_MALFORMED, out);
    }

    memcpy(&rx->buffer[rx->received_len], &frame->data[1], take);
    rx->received_len = (uint16_t)(rx->received_len + take);
    rx->next_sn = (uint8_t)((rx->next_sn + 1u) & 0x0Fu);
    rx->last_frame_us = now_us;

    if (rx->received_len >= rx->expected_len) {
        rx->state = ISOTP_RX_IDLE;
        rx->messages_completed++;
        out->message_complete = true;
        out->message = rx->buffer;
        out->message_len = rx->expected_len;
        return ISOTP_OK;
    }

    if (rx->block_size != 0u) {
        rx->block_remaining = (uint16_t)(rx->block_remaining - 1u);
        if (rx->block_remaining == 0u) {
            rx->block_remaining = (uint16_t)rx->block_size;
            rx_emit_fc(rx, ISOTP_FS_CONTINUE, out);
        }
    }
    return ISOTP_OK;
}

isotp_err_t isotp_rx_on_frame(isotp_rx_t *rx, const hal_can_frame_t *frame, uint64_t now_us,
                              isotp_rx_outcome_t *out)
{
    if (rx == NULL || frame == NULL || out == NULL) {
        return ISOTP_ERR_INVALID_ARG;
    }
    outcome_rx_clear(out);

    /* Most traffic on a shared bus belongs to someone else; that is not an error. */
    if (frame->id != rx->rx_can_id) {
        return ISOTP_OK;
    }
    if (frame->dlc == 0u) {
        return rx_fail(rx, ISOTP_ERR_MALFORMED, out);
    }

    switch ((uint8_t)(frame->data[0] >> 4)) {
    case (uint8_t)ISOTP_PCI_SINGLE:
        return rx_on_single(rx, frame, now_us, out);
    case (uint8_t)ISOTP_PCI_FIRST:
        return rx_on_first(rx, frame, now_us, out);
    case (uint8_t)ISOTP_PCI_CONSECUTIVE:
        return rx_on_consecutive(rx, frame, now_us, out);
    case (uint8_t)ISOTP_PCI_FLOW_CONTROL:
        return ISOTP_OK; /* The sender's business, not the receiver's. */
    default:
        return rx_fail(rx, ISOTP_ERR_MALFORMED, out);
    }
}

isotp_err_t isotp_rx_on_tick(isotp_rx_t *rx, uint64_t now_us, isotp_rx_outcome_t *out)
{
    if (rx == NULL || out == NULL) {
        return ISOTP_ERR_INVALID_ARG;
    }
    outcome_rx_clear(out);

    if (rx->state != ISOTP_RX_ASSEMBLING) {
        return ISOTP_OK;
    }
    if (!elapsed(now_us, rx->last_frame_us, (uint32_t)ISOTP_N_CR_TIMEOUT_US)) {
        return ISOTP_OK;
    }
    return rx_fail(rx, ISOTP_ERR_TIMEOUT_N_CR, out);
}

/* ------------------------------------------------------------------- sender ------- */

void isotp_tx_init(isotp_tx_t *tx, uint32_t tx_can_id, uint32_t fc_can_id)
{
    if (tx == NULL) {
        return;
    }
    memset(tx, 0, sizeof(*tx));
    tx->tx_can_id = tx_can_id;
    tx->fc_can_id = fc_can_id;
    tx->state = ISOTP_TX_IDLE;
}

void isotp_tx_reset(isotp_tx_t *tx)
{
    if (tx == NULL) {
        return;
    }
    tx->state = ISOTP_TX_IDLE;
    tx->total_len = 0u;
    tx->sent_len = 0u;
    tx->next_sn = 0u;
    tx->block_size = 0u;
    tx->block_remaining = 0u;
    tx->st_min_us = 0u;
    tx->next_cf_due_us = 0u;
}

bool isotp_tx_is_idle(const isotp_tx_t *tx)
{
    return tx == NULL || tx->state == ISOTP_TX_IDLE;
}

static isotp_err_t tx_fail(isotp_tx_t *tx, isotp_err_t error, isotp_tx_outcome_t *out)
{
    tx->errors++;
    isotp_tx_reset(tx);
    out->error = true;
    out->error_code = error;
    return error;
}

isotp_err_t isotp_tx_start(isotp_tx_t *tx, const uint8_t *data, uint16_t len, uint64_t now_us,
                           isotp_tx_outcome_t *out)
{
    if (tx == NULL || data == NULL || out == NULL) {
        return ISOTP_ERR_INVALID_ARG;
    }
    outcome_tx_clear(out);

    if (tx->state != ISOTP_TX_IDLE) {
        return ISOTP_ERR_BUSY;
    }
    if (len == 0u || len > (uint16_t)ISOTP_TX_BUFFER_SIZE) {
        return ISOTP_ERR_INVALID_ARG;
    }

    memcpy(tx->buffer, data, len);
    tx->total_len = len;
    tx->sent_len = 0u;
    tx->next_sn = 1u;

    uint8_t payload[HAL_CAN_MAX_DLC];

    if (len <= (uint16_t)ISOTP_SF_MAX_PAYLOAD) {
        payload[0] = (uint8_t)(((uint8_t)ISOTP_PCI_SINGLE << 4) | (uint8_t)len);
        memcpy(&payload[1], data, len);
        frame_build(&out->frame, tx->tx_can_id, payload, (uint8_t)(1u + len));
        out->send_frame = true;
        out->message_sent = true;
        tx->sent_len = len;
        tx->messages_sent++;
        tx->state = ISOTP_TX_IDLE;
        return ISOTP_OK;
    }

    payload[0] = (uint8_t)(((uint8_t)ISOTP_PCI_FIRST << 4) | (uint8_t)((len >> 8) & 0x0Fu));
    payload[1] = (uint8_t)(len & 0xFFu);
    memcpy(&payload[2], data, (size_t)ISOTP_FF_PAYLOAD);
    frame_build(&out->frame, tx->tx_can_id, payload, (uint8_t)HAL_CAN_MAX_DLC);
    out->send_frame = true;

    tx->sent_len = (uint16_t)ISOTP_FF_PAYLOAD;
    tx->state = ISOTP_TX_WAIT_FC;
    tx->last_event_us = now_us;
    return ISOTP_OK;
}

isotp_err_t isotp_tx_on_frame(isotp_tx_t *tx, const hal_can_frame_t *frame, uint64_t now_us,
                              isotp_tx_outcome_t *out)
{
    if (tx == NULL || frame == NULL || out == NULL) {
        return ISOTP_ERR_INVALID_ARG;
    }
    outcome_tx_clear(out);

    if (frame->id != tx->fc_can_id || frame->dlc == 0u) {
        return ISOTP_OK;
    }
    if ((uint8_t)(frame->data[0] >> 4) != (uint8_t)ISOTP_PCI_FLOW_CONTROL) {
        return ISOTP_OK; /* Reassembly traffic; the receiver's business. */
    }
    if (tx->state != ISOTP_TX_WAIT_FC) {
        return ISOTP_OK; /* Unsolicited flow control; nothing is waiting on it. */
    }
    if (frame->dlc < 3u) {
        return tx_fail(tx, ISOTP_ERR_MALFORMED, out);
    }

    switch ((uint8_t)(frame->data[0] & 0x0Fu)) {
    case (uint8_t)ISOTP_FS_CONTINUE:
        tx->block_size = frame->data[1];
        tx->st_min_us = isotp_decode_stmin_us(frame->data[2]);
        tx->block_remaining = (tx->block_size == 0u) ? 0u : (uint16_t)tx->block_size;
        tx->state = ISOTP_TX_SENDING;
        /* Separation time applies between consecutive frames, so the first may go at once. */
        tx->next_cf_due_us = now_us;
        tx->last_event_us = now_us;
        return ISOTP_OK;

    case (uint8_t)ISOTP_FS_WAIT:
        tx->last_event_us = now_us; /* Restart the wait; the peer is alive. */
        return ISOTP_OK;

    case (uint8_t)ISOTP_FS_OVERFLOW:
        return tx_fail(tx, ISOTP_ERR_OVERFLOW, out);

    default:
        return tx_fail(tx, ISOTP_ERR_MALFORMED, out);
    }
}

isotp_err_t isotp_tx_on_tick(isotp_tx_t *tx, uint64_t now_us, isotp_tx_outcome_t *out)
{
    if (tx == NULL || out == NULL) {
        return ISOTP_ERR_INVALID_ARG;
    }
    outcome_tx_clear(out);

    if (tx->state == ISOTP_TX_WAIT_FC) {
        if (elapsed(now_us, tx->last_event_us, (uint32_t)ISOTP_N_BS_TIMEOUT_US)) {
            return tx_fail(tx, ISOTP_ERR_TIMEOUT_N_BS, out);
        }
        return ISOTP_OK;
    }
    if (tx->state != ISOTP_TX_SENDING) {
        return ISOTP_OK;
    }
    if (now_us < tx->next_cf_due_us) {
        return ISOTP_OK; /* Separation time has not elapsed. */
    }

    const uint16_t remaining = (uint16_t)(tx->total_len - tx->sent_len);
    const uint16_t take =
        (remaining < (uint16_t)ISOTP_CF_MAX_PAYLOAD) ? remaining : (uint16_t)ISOTP_CF_MAX_PAYLOAD;

    uint8_t payload[HAL_CAN_MAX_DLC];
    payload[0] = (uint8_t)(((uint8_t)ISOTP_PCI_CONSECUTIVE << 4) | tx->next_sn);
    memcpy(&payload[1], &tx->buffer[tx->sent_len], take);
    frame_build(&out->frame, tx->tx_can_id, payload, (uint8_t)(1u + take));
    out->send_frame = true;

    tx->sent_len = (uint16_t)(tx->sent_len + take);
    tx->next_sn = (uint8_t)((tx->next_sn + 1u) & 0x0Fu);
    tx->next_cf_due_us = now_us + (uint64_t)tx->st_min_us;
    tx->last_event_us = now_us;

    if (tx->sent_len >= tx->total_len) {
        tx->state = ISOTP_TX_IDLE;
        tx->messages_sent++;
        out->message_sent = true;
        return ISOTP_OK;
    }

    if (tx->block_size != 0u) {
        tx->block_remaining = (uint16_t)(tx->block_remaining - 1u);
        if (tx->block_remaining == 0u) {
            tx->state = ISOTP_TX_WAIT_FC;
            tx->last_event_us = now_us;
        }
    }
    return ISOTP_OK;
}
