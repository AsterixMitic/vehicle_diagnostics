/**
 * @file test_isotp.c
 * @brief Unit tests for the ISO 15765-2 transport layer.
 *
 * Every timeout and every separation time here is driven purely by the timestamp passed in, so
 * the whole suite runs in microseconds with no waiting, no scheduler and no hardware. That is
 * the point of the layer performing no input or output.
 */
#include "test_isotp.h"

#include <stdio.h>
#include <string.h>

#include "isotp.h"

static int s_failures;
static int s_checks;

#define CHECK(condition, description)                                        \
    do {                                                                     \
        s_checks++;                                                          \
        if (!(condition)) {                                                  \
            s_failures++;                                                    \
            printf("  FAIL %s:%d  %s\n", __func__, __LINE__, (description)); \
        }                                                                    \
    } while (0)

/* Tester sends on 0x7E0; the ECU answers on 0x7E8. */
#define TESTER_ID 0x7E0u
#define ECU_ID 0x7E8u

/** Build a frame padded to eight bytes, as it appears on the wire. */
static hal_can_frame_t wire_frame(uint32_t id, const uint8_t *payload, uint8_t len)
{
    hal_can_frame_t frame;
    memset(&frame, 0, sizeof(frame));
    frame.id = id;
    frame.dlc = (uint8_t)HAL_CAN_MAX_DLC;
    for (uint8_t i = 0; i < len; i++) {
        frame.data[i] = payload[i];
    }
    for (uint8_t i = len; i < (uint8_t)HAL_CAN_MAX_DLC; i++) {
        frame.data[i] = 0xAAu;
    }
    return frame;
}

/** Build a frame with an explicit, possibly short, length. */
static hal_can_frame_t short_frame(uint32_t id, const uint8_t *payload, uint8_t dlc)
{
    hal_can_frame_t frame = wire_frame(id, payload, dlc);
    frame.dlc = dlc;
    return frame;
}

static void fill_pattern(uint8_t *buffer, uint16_t len)
{
    for (uint16_t i = 0; i < len; i++) {
        buffer[i] = (uint8_t)((i * 7u + 3u) & 0xFFu);
    }
}

/* ------------------------------------------------------- separation time codec ---- */

static void test_stmin_decoding_covers_every_range(void)
{
    CHECK(isotp_decode_stmin_us(0x00u) == 0u, "zero means no separation");
    CHECK(isotp_decode_stmin_us(0x01u) == 1000u, "one is one millisecond");
    CHECK(isotp_decode_stmin_us(0x14u) == 20000u, "twenty is twenty milliseconds");
    CHECK(isotp_decode_stmin_us(0x7Fu) == 127000u, "127 is the millisecond maximum");

    CHECK(isotp_decode_stmin_us(0xF1u) == 100u, "0xF1 is one hundred microseconds");
    CHECK(isotp_decode_stmin_us(0xF5u) == 500u, "0xF5 is five hundred microseconds");
    CHECK(isotp_decode_stmin_us(0xF9u) == 900u, "0xF9 is nine hundred microseconds");

    /* Reserved values must be slow, not fast: treating them as zero would flood the peer. */
    CHECK(isotp_decode_stmin_us(0x80u) == ISOTP_STMIN_MAX_US, "reserved falls back to the maximum");
    CHECK(isotp_decode_stmin_us(0xF0u) == ISOTP_STMIN_MAX_US, "0xF0 is reserved");
    CHECK(isotp_decode_stmin_us(0xFAu) == ISOTP_STMIN_MAX_US, "0xFA is reserved");
    CHECK(isotp_decode_stmin_us(0xFFu) == ISOTP_STMIN_MAX_US, "0xFF is reserved");
}

/* ------------------------------------------------------------------ receiver ------ */

static void test_single_frame_of_every_valid_length(void)
{
    for (uint8_t len = 1u; len <= 7u; len++) {
        isotp_rx_t rx;
        isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);

        uint8_t payload[8];
        payload[0] = len; /* PCI single frame, low nibble is the length */
        for (uint8_t i = 0; i < len; i++) {
            payload[1u + i] = (uint8_t)(0x10u + i);
        }
        const hal_can_frame_t frame = wire_frame(ECU_ID, payload, (uint8_t)(1u + len));

        isotp_rx_outcome_t out;
        CHECK(isotp_rx_on_frame(&rx, &frame, 0u, &out) == ISOTP_OK, "single frame is accepted");
        CHECK(out.message_complete, "a single frame completes on arrival");
        CHECK(out.message_len == len, "the declared length is delivered");
        CHECK(!out.send_frame, "a single frame needs no flow control");
        CHECK(!out.error, "a valid single frame is not an error");

        bool content_ok = out.message_complete;
        for (uint8_t i = 0; i < len && out.message_complete; i++) {
            content_ok = content_ok && (out.message[i] == (uint8_t)(0x10u + i));
        }
        CHECK(content_ok, "payload is delivered intact");
        CHECK(isotp_rx_is_idle(&rx), "the receiver is idle afterwards");
    }
}

static void test_single_frame_rejections(void)
{
    isotp_rx_t rx;
    isotp_rx_outcome_t out;

    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);
    const uint8_t zero_len[2] = {0x00u, 0x11u};
    hal_can_frame_t frame = wire_frame(ECU_ID, zero_len, 2u);
    CHECK(isotp_rx_on_frame(&rx, &frame, 0u, &out) == ISOTP_ERR_MALFORMED,
          "a single frame declaring nothing is malformed");
    CHECK(!out.message_complete, "nothing is delivered from it");

    /* Declared length longer than a frame can carry. */
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);
    const uint8_t too_long[2] = {0x08u, 0x11u};
    frame = wire_frame(ECU_ID, too_long, 2u);
    CHECK(isotp_rx_on_frame(&rx, &frame, 0u, &out) == ISOTP_ERR_MALFORMED,
          "a single frame declaring more than seven is malformed");

    /* Declared length longer than the frame actually holds. */
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);
    const uint8_t truncated[3] = {0x05u, 0x11u, 0x22u};
    frame = short_frame(ECU_ID, truncated, 3u);
    CHECK(isotp_rx_on_frame(&rx, &frame, 0u, &out) == ISOTP_ERR_MALFORMED,
          "a frame shorter than its declaration is malformed");
}

static void test_foreign_and_flow_control_frames_are_ignored(void)
{
    isotp_rx_t rx;
    isotp_rx_outcome_t out;
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);

    const uint8_t single[2] = {0x01u, 0x42u};
    hal_can_frame_t other = wire_frame(0x123u, single, 2u);
    CHECK(isotp_rx_on_frame(&rx, &other, 0u, &out) == ISOTP_OK,
          "traffic for another identifier is not an error");
    CHECK(!out.message_complete && !out.error && !out.send_frame, "and produces nothing");

    const uint8_t fc[3] = {0x30u, 0x00u, 0x00u};
    hal_can_frame_t flow = wire_frame(ECU_ID, fc, 3u);
    CHECK(isotp_rx_on_frame(&rx, &flow, 0u, &out) == ISOTP_OK,
          "flow control is the sender's business, not an error here");
    CHECK(!out.error, "and is not counted against the receiver");
    CHECK(rx.errors == 0u, "no error was counted for either");
}

/** Feed a first frame plus consecutive frames and return the receiver's verdict. */
static void feed_segmented(isotp_rx_t *rx, const uint8_t *message, uint16_t len, uint64_t start_us,
                           uint8_t *delivered, uint16_t *delivered_len, uint32_t *flow_controls)
{
    isotp_rx_outcome_t out;
    *delivered_len = 0u;
    *flow_controls = 0u;

    uint8_t payload[8];
    payload[0] = (uint8_t)(0x10u | (uint8_t)((len >> 8) & 0x0Fu));
    payload[1] = (uint8_t)(len & 0xFFu);
    memcpy(&payload[2], message, (size_t)ISOTP_FF_PAYLOAD);
    hal_can_frame_t frame = wire_frame(ECU_ID, payload, 8u);

    (void)isotp_rx_on_frame(rx, &frame, start_us, &out);
    if (out.send_frame) {
        (*flow_controls)++;
    }

    uint16_t offset = (uint16_t)ISOTP_FF_PAYLOAD;
    uint8_t sn = 1u;
    uint64_t now = start_us;

    while (offset < len) {
        const uint16_t remaining = (uint16_t)(len - offset);
        const uint16_t take = remaining < 7u ? remaining : 7u;

        payload[0] = (uint8_t)(0x20u | sn);
        memcpy(&payload[1], &message[offset], take);
        frame = wire_frame(ECU_ID, payload, (uint8_t)(1u + take));

        now += 1000u;
        (void)isotp_rx_on_frame(rx, &frame, now, &out);
        if (out.send_frame) {
            (*flow_controls)++;
        }
        if (out.message_complete) {
            *delivered_len = out.message_len;
            memcpy(delivered, out.message, out.message_len);
            return;
        }

        offset = (uint16_t)(offset + take);
        sn = (uint8_t)((sn + 1u) & 0x0Fu);
    }
}

static void test_multi_frame_reassembly(void)
{
    const uint16_t lengths[] = {8u, 13u, 20u, 62u, 150u, 511u};

    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); i++) {
        const uint16_t len = lengths[i];
        uint8_t message[ISOTP_RX_BUFFER_SIZE];
        fill_pattern(message, len);

        isotp_rx_t rx;
        isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);

        uint8_t delivered[ISOTP_RX_BUFFER_SIZE];
        uint16_t delivered_len = 0u;
        uint32_t flow_controls = 0u;
        feed_segmented(&rx, message, len, 1000u, delivered, &delivered_len, &flow_controls);

        CHECK(delivered_len == len, "the declared length is delivered");
        CHECK(delivered_len == len && memcmp(delivered, message, len) == 0,
              "the reassembled message matches the original byte for byte");
        CHECK(flow_controls == 1u, "block size zero exchanges flow control exactly once");
        CHECK(isotp_rx_is_idle(&rx), "the receiver is idle after completing");
    }
}

static void test_sequence_number_wraps_past_fifteen(void)
{
    /* Six bytes in the first frame plus sixteen consecutive frames carries the sequence
     * number past fifteen and round to zero, which is the classic place to get this wrong. */
    const uint16_t len = 150u;
    uint8_t message[200];
    fill_pattern(message, len);

    isotp_rx_t rx;
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);

    uint8_t delivered[ISOTP_RX_BUFFER_SIZE];
    uint16_t delivered_len = 0u;
    uint32_t flow_controls = 0u;
    feed_segmented(&rx, message, len, 1000u, delivered, &delivered_len, &flow_controls);

    CHECK(delivered_len == len, "a message spanning the wrap completes");
    CHECK(delivered_len == len && memcmp(delivered, message, len) == 0,
          "content survives the sequence number wrapping to zero");
}

static void test_padding_is_discarded(void)
{
    isotp_rx_t rx;
    isotp_rx_outcome_t out;
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);

    /* Nine bytes: six in the first frame, three in a consecutive frame padded to eight. */
    uint8_t payload[8] = {0x10u, 0x09u, 1u, 2u, 3u, 4u, 5u, 6u};
    hal_can_frame_t frame = wire_frame(ECU_ID, payload, 8u);
    (void)isotp_rx_on_frame(&rx, &frame, 0u, &out);

    const uint8_t cf[4] = {0x21u, 7u, 8u, 9u};
    frame = wire_frame(ECU_ID, cf, 4u); /* padded to eight with 0xAA */
    (void)isotp_rx_on_frame(&rx, &frame, 1000u, &out);

    CHECK(out.message_complete, "the message completes on the padded frame");
    CHECK(out.message_len == 9u, "the declared length wins over the padding");
    bool ok = out.message_complete && out.message_len == 9u;
    for (uint8_t i = 0; i < 9u && ok; i++) {
        ok = ok && (out.message[i] == (uint8_t)(i + 1u));
    }
    CHECK(ok, "no padding byte reaches the delivered message");
}

static void test_first_frame_rejections(void)
{
    isotp_rx_t rx;
    isotp_rx_outcome_t out;

    /* Declares seven, which should have been a single frame. */
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);
    uint8_t payload[8] = {0x10u, 0x07u, 1u, 2u, 3u, 4u, 5u, 6u};
    hal_can_frame_t frame = wire_frame(ECU_ID, payload, 8u);
    CHECK(isotp_rx_on_frame(&rx, &frame, 0u, &out) == ISOTP_ERR_MALFORMED,
          "a first frame declaring seven or fewer is malformed");
    CHECK(isotp_rx_is_idle(&rx), "and starts no reassembly");

    /* Declares zero, the 2016 escape sequence, which this layer does not implement. */
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);
    payload[0] = 0x10u;
    payload[1] = 0x00u;
    frame = wire_frame(ECU_ID, payload, 8u);
    CHECK(isotp_rx_on_frame(&rx, &frame, 0u, &out) == ISOTP_ERR_UNSUPPORTED,
          "the escape sequence is refused, not misparsed as payload");

    /* A first frame must occupy a full frame. */
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);
    const uint8_t stunted[4] = {0x10u, 0x20u, 1u, 2u};
    frame = short_frame(ECU_ID, stunted, 4u);
    CHECK(isotp_rx_on_frame(&rx, &frame, 0u, &out) == ISOTP_ERR_MALFORMED,
          "a short first frame is malformed");
}

static void test_oversized_transfer_is_refused_and_recovers(void)
{
    isotp_rx_t rx;
    isotp_rx_outcome_t out;
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);

    /* Declare 4000 bytes against a buffer that holds far less. */
    const uint16_t huge = 4000u;
    uint8_t payload[8];
    payload[0] = (uint8_t)(0x10u | (uint8_t)((huge >> 8) & 0x0Fu));
    payload[1] = (uint8_t)(huge & 0xFFu);
    for (uint8_t i = 2u; i < 8u; i++) {
        payload[i] = i;
    }
    hal_can_frame_t frame = wire_frame(ECU_ID, payload, 8u);

    CHECK(isotp_rx_on_frame(&rx, &frame, 0u, &out) == ISOTP_ERR_OVERFLOW,
          "an oversized declaration is refused");
    CHECK(out.send_frame, "and is answered with flow control");
    CHECK(out.send_frame && (out.frame.data[0] == 0x32u),
          "the flow control carries the overflow status");
    CHECK(out.send_frame && out.frame.id == TESTER_ID, "sent back to the peer");
    CHECK(!out.message_complete, "nothing is delivered");
    CHECK(isotp_rx_is_idle(&rx), "and no reassembly is left in progress");

    /* The receiver must still work afterwards. */
    uint8_t message[20];
    fill_pattern(message, 20u);
    uint8_t delivered[ISOTP_RX_BUFFER_SIZE];
    uint16_t delivered_len = 0u;
    uint32_t flow_controls = 0u;
    feed_segmented(&rx, message, 20u, 1000u, delivered, &delivered_len, &flow_controls);
    CHECK(delivered_len == 20u, "an acceptable transfer still works after a refusal");
    CHECK(delivered_len == 20u && memcmp(delivered, message, 20u) == 0, "and is intact");
}

static void test_sequence_errors_abandon_the_transfer(void)
{
    isotp_rx_t rx;
    isotp_rx_outcome_t out;

    /* A consecutive frame with nothing in progress. */
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);
    const uint8_t stray[3] = {0x21u, 0x11u, 0x22u};
    hal_can_frame_t frame = wire_frame(ECU_ID, stray, 3u);
    CHECK(isotp_rx_on_frame(&rx, &frame, 0u, &out) == ISOTP_ERR_UNEXPECTED_FRAME,
          "a consecutive frame while idle is unexpected");

    /* Out of order mid-transfer. */
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);
    uint8_t payload[8] = {0x10u, 0x14u, 1u, 2u, 3u, 4u, 5u, 6u};
    frame = wire_frame(ECU_ID, payload, 8u);
    (void)isotp_rx_on_frame(&rx, &frame, 0u, &out);
    CHECK(!isotp_rx_is_idle(&rx), "reassembly is in progress");

    const uint8_t wrong_sn[8] = {0x23u, 7u, 8u, 9u, 10u, 11u, 12u, 13u}; /* expected 0x21 */
    frame = wire_frame(ECU_ID, wrong_sn, 8u);
    CHECK(isotp_rx_on_frame(&rx, &frame, 1000u, &out) == ISOTP_ERR_WRONG_SEQUENCE,
          "an out-of-order consecutive frame is rejected");
    CHECK(!out.message_complete, "nothing is delivered");
    CHECK(isotp_rx_is_idle(&rx), "and the transfer is abandoned, ready for a new one");
}

static void test_block_size_drives_flow_control(void)
{
    isotp_rx_t rx;
    isotp_rx_outcome_t out;
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 3u, 0x14u); /* block size three, 20 ms separation */

    const uint16_t len = 60u;
    uint8_t message[100];
    fill_pattern(message, len);

    uint8_t payload[8];
    payload[0] = 0x10u;
    payload[1] = (uint8_t)len;
    memcpy(&payload[2], message, 6u);
    hal_can_frame_t frame = wire_frame(ECU_ID, payload, 8u);
    (void)isotp_rx_on_frame(&rx, &frame, 0u, &out);

    CHECK(out.send_frame, "the first frame is answered with flow control");
    CHECK(out.send_frame && out.frame.data[0] == 0x30u, "carrying continue-to-send");
    CHECK(out.send_frame && out.frame.data[1] == 3u, "the configured block size");
    CHECK(out.send_frame && out.frame.data[2] == 0x14u, "and the configured separation time");

    uint16_t offset = 6u;
    uint8_t sn = 1u;
    uint32_t flow_controls = 0u;
    uint32_t frames_since_fc = 0u;
    bool boundary_correct = true;
    uint64_t now = 0u;

    while (offset < len) {
        const uint16_t remaining = (uint16_t)(len - offset);
        const uint16_t take = remaining < 7u ? remaining : 7u;
        payload[0] = (uint8_t)(0x20u | sn);
        memcpy(&payload[1], &message[offset], take);
        frame = wire_frame(ECU_ID, payload, (uint8_t)(1u + take));

        now += 1000u;
        (void)isotp_rx_on_frame(&rx, &frame, now, &out);
        frames_since_fc++;
        offset = (uint16_t)(offset + take);
        sn = (uint8_t)((sn + 1u) & 0x0Fu);

        if (out.message_complete) {
            break;
        }
        if (out.send_frame) {
            flow_controls++;
            boundary_correct = boundary_correct && (frames_since_fc == 3u);
            frames_since_fc = 0u;
        }
    }

    CHECK(flow_controls >= 2u, "flow control repeats at each block boundary");
    CHECK(boundary_correct, "and arrives after exactly the granted number of frames");
}

static void test_receiver_timeout(void)
{
    isotp_rx_t rx;
    isotp_rx_outcome_t out;
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);

    const uint64_t t0 = 5000000u;
    uint8_t payload[8] = {0x10u, 0x14u, 1u, 2u, 3u, 4u, 5u, 6u};
    hal_can_frame_t frame = wire_frame(ECU_ID, payload, 8u);
    (void)isotp_rx_on_frame(&rx, &frame, t0, &out);

    CHECK(isotp_rx_on_tick(&rx, t0 + 999999u, &out) == ISOTP_OK, "just inside the timeout is fine");
    CHECK(!out.error, "and reports nothing");
    CHECK(!isotp_rx_is_idle(&rx), "the transfer is still alive");

    CHECK(isotp_rx_on_tick(&rx, t0 + 1000000u, &out) == ISOTP_ERR_TIMEOUT_N_CR,
          "the receiver gives up once the timeout expires");
    CHECK(out.error && out.error_code == ISOTP_ERR_TIMEOUT_N_CR, "reporting which timeout");
    CHECK(isotp_rx_is_idle(&rx), "and returns to idle");
}

static void test_progress_does_not_time_out(void)
{
    isotp_rx_t rx;
    isotp_rx_outcome_t out;
    isotp_rx_init(&rx, ECU_ID, TESTER_ID, 0u, 0u);

    uint64_t now = 1000u;
    uint8_t payload[8] = {0x10u, 0x28u, 1u, 2u, 3u, 4u, 5u, 6u}; /* forty bytes */
    hal_can_frame_t frame = wire_frame(ECU_ID, payload, 8u);
    (void)isotp_rx_on_frame(&rx, &frame, now, &out);

    bool timed_out = false;
    uint8_t sn = 1u;
    for (int i = 0; i < 5; i++) {
        now += 900000u; /* under the timeout each time, but far over it in total */
        (void)isotp_rx_on_tick(&rx, now, &out);
        timed_out = timed_out || out.error;

        payload[0] = (uint8_t)(0x20u | sn);
        frame = wire_frame(ECU_ID, payload, 8u);
        (void)isotp_rx_on_frame(&rx, &frame, now, &out);
        sn = (uint8_t)((sn + 1u) & 0x0Fu);
        if (out.message_complete) {
            break;
        }
    }

    CHECK(!timed_out, "steady progress never trips the timeout however long the transfer runs");
}

/* -------------------------------------------------------------------- sender ------ */

static void test_short_message_sends_as_one_frame(void)
{
    for (uint16_t len = 1u; len <= 7u; len++) {
        isotp_tx_t tx;
        isotp_tx_init(&tx, TESTER_ID, ECU_ID);

        uint8_t message[8];
        fill_pattern(message, len);

        isotp_tx_outcome_t out;
        CHECK(isotp_tx_start(&tx, message, len, 0u, &out) == ISOTP_OK, "a short message starts");
        CHECK(out.send_frame, "producing one frame");
        CHECK(out.message_sent, "and completing immediately");
        CHECK(out.send_frame && out.frame.data[0] == (uint8_t)len, "which declares the length");
        CHECK(out.send_frame && out.frame.id == TESTER_ID, "on the transmit identifier");
        CHECK(isotp_tx_is_idle(&tx), "leaving the sender idle");
    }
}

static void test_long_message_waits_for_flow_control(void)
{
    isotp_tx_t tx;
    isotp_tx_init(&tx, TESTER_ID, ECU_ID);

    uint8_t message[40];
    fill_pattern(message, 40u);

    isotp_tx_outcome_t out;
    CHECK(isotp_tx_start(&tx, message, 40u, 1000u, &out) == ISOTP_OK, "a long message starts");
    CHECK(out.send_frame, "producing a first frame");
    CHECK(out.send_frame && out.frame.data[0] == 0x10u, "with the first-frame indicator");
    CHECK(out.send_frame && out.frame.data[1] == 40u, "declaring the total length");
    CHECK(!out.message_sent, "the message is not complete yet");

    /* Nothing more may go until the peer grants it. */
    (void)isotp_tx_on_tick(&tx, 1000u + 500000u, &out);
    CHECK(!out.send_frame, "no consecutive frame is produced before flow control arrives");
    CHECK(!out.error, "and nothing has failed yet");

    CHECK(isotp_tx_start(&tx, message, 40u, 1000u, &out) == ISOTP_ERR_BUSY,
          "a second message cannot start while one is in flight");
}

static void test_flow_control_statuses(void)
{
    uint8_t message[40];
    fill_pattern(message, 40u);
    isotp_tx_outcome_t out;

    /* Continue to send. */
    isotp_tx_t tx;
    isotp_tx_init(&tx, TESTER_ID, ECU_ID);
    (void)isotp_tx_start(&tx, message, 40u, 0u, &out);
    const uint8_t cts[3] = {0x30u, 0x00u, 0x00u};
    hal_can_frame_t fc = wire_frame(ECU_ID, cts, 3u);
    CHECK(isotp_tx_on_frame(&tx, &fc, 0u, &out) == ISOTP_OK, "continue-to-send is accepted");
    CHECK(!out.send_frame, "flow control itself emits nothing");
    (void)isotp_tx_on_tick(&tx, 0u, &out);
    CHECK(out.send_frame, "after which consecutive frames flow");
    CHECK(out.send_frame && (out.frame.data[0] == 0x21u), "starting at sequence number one");

    /* Wait defers without abandoning. */
    isotp_tx_init(&tx, TESTER_ID, ECU_ID);
    (void)isotp_tx_start(&tx, message, 40u, 0u, &out);
    const uint8_t wait[3] = {0x31u, 0x00u, 0x00u};
    fc = wire_frame(ECU_ID, wait, 3u);
    CHECK(isotp_tx_on_frame(&tx, &fc, 0u, &out) == ISOTP_OK, "wait is accepted");
    CHECK(!out.error, "and does not abandon the transfer");
    (void)isotp_tx_on_tick(&tx, 500000u, &out);
    CHECK(!out.send_frame, "nothing is emitted while waiting");
    CHECK(!isotp_tx_is_idle(&tx), "and the transfer stays alive");

    /* Overflow abandons. */
    isotp_tx_init(&tx, TESTER_ID, ECU_ID);
    (void)isotp_tx_start(&tx, message, 40u, 0u, &out);
    const uint8_t overflow[3] = {0x32u, 0x00u, 0x00u};
    fc = wire_frame(ECU_ID, overflow, 3u);
    CHECK(isotp_tx_on_frame(&tx, &fc, 0u, &out) == ISOTP_ERR_OVERFLOW,
          "overflow abandons the transfer");
    CHECK(out.error && out.error_code == ISOTP_ERR_OVERFLOW, "reporting overflow");
    CHECK(isotp_tx_is_idle(&tx), "and returns the sender to idle");
}

static void test_block_size_limits_the_burst(void)
{
    isotp_tx_t tx;
    isotp_tx_init(&tx, TESTER_ID, ECU_ID);

    uint8_t message[60];
    fill_pattern(message, 60u);

    isotp_tx_outcome_t out;
    (void)isotp_tx_start(&tx, message, 60u, 0u, &out);

    const uint8_t cts[3] = {0x30u, 0x03u, 0x00u}; /* grant three frames */
    hal_can_frame_t fc = wire_frame(ECU_ID, cts, 3u);
    (void)isotp_tx_on_frame(&tx, &fc, 0u, &out);

    uint32_t emitted = 0u;
    for (int i = 0; i < 10; i++) {
        (void)isotp_tx_on_tick(&tx, (uint64_t)(i + 1) * 1000u, &out);
        if (out.send_frame) {
            emitted++;
        }
    }
    CHECK(emitted == 3u, "exactly the granted number of frames is emitted");
    CHECK(!isotp_tx_is_idle(&tx), "and the sender waits for the next flow control");

    (void)isotp_tx_on_frame(&tx, &fc, 20000u, &out);
    emitted = 0u;
    for (int i = 0; i < 10; i++) {
        (void)isotp_tx_on_tick(&tx, 20000u + (uint64_t)(i + 1) * 1000u, &out);
        if (out.send_frame) {
            emitted++;
        }
    }
    CHECK(emitted == 3u, "the next grant releases exactly three more");
}

static void test_separation_time_is_respected(void)
{
    isotp_tx_t tx;
    isotp_tx_init(&tx, TESTER_ID, ECU_ID);

    uint8_t message[40];
    fill_pattern(message, 40u);

    const uint64_t t0 = 5000000u;
    isotp_tx_outcome_t out;
    (void)isotp_tx_start(&tx, message, 40u, t0, &out);

    const uint8_t cts[3] = {0x30u, 0x00u, 0x14u}; /* twenty milliseconds */
    hal_can_frame_t fc = wire_frame(ECU_ID, cts, 3u);
    (void)isotp_tx_on_frame(&tx, &fc, t0, &out);

    (void)isotp_tx_on_tick(&tx, t0, &out);
    CHECK(out.send_frame, "the first consecutive frame may go immediately after flow control");

    (void)isotp_tx_on_tick(&tx, t0 + 19999u, &out);
    CHECK(!out.send_frame, "no frame before the separation time has elapsed");

    (void)isotp_tx_on_tick(&tx, t0 + 20000u, &out);
    CHECK(out.send_frame, "and one once it has");

    /* The microsecond range must also be honoured, not rounded to nothing. */
    isotp_tx_init(&tx, TESTER_ID, ECU_ID);
    (void)isotp_tx_start(&tx, message, 40u, t0, &out);
    const uint8_t fast[3] = {0x30u, 0x00u, 0xF5u}; /* five hundred microseconds */
    fc = wire_frame(ECU_ID, fast, 3u);
    (void)isotp_tx_on_frame(&tx, &fc, t0, &out);
    (void)isotp_tx_on_tick(&tx, t0, &out);
    CHECK(out.send_frame, "first frame goes at once");
    (void)isotp_tx_on_tick(&tx, t0 + 499u, &out);
    CHECK(!out.send_frame, "the microsecond separation is honoured");
    (void)isotp_tx_on_tick(&tx, t0 + 500u, &out);
    CHECK(out.send_frame, "and released on time");
}

static void test_sender_timeout(void)
{
    isotp_tx_t tx;
    isotp_tx_init(&tx, TESTER_ID, ECU_ID);

    uint8_t message[40];
    fill_pattern(message, 40u);

    const uint64_t t0 = 9000000u;
    isotp_tx_outcome_t out;
    (void)isotp_tx_start(&tx, message, 40u, t0, &out);

    CHECK(isotp_tx_on_tick(&tx, t0 + 999999u, &out) == ISOTP_OK, "just inside the timeout is fine");
    CHECK(!out.error, "and reports nothing");

    CHECK(isotp_tx_on_tick(&tx, t0 + 1000000u, &out) == ISOTP_ERR_TIMEOUT_N_BS,
          "the sender gives up when flow control never arrives");
    CHECK(out.error && out.error_code == ISOTP_ERR_TIMEOUT_N_BS, "reporting which timeout");
    CHECK(isotp_tx_is_idle(&tx), "and returns to idle");
}

static void test_sender_rejects_bad_arguments(void)
{
    isotp_tx_t tx;
    isotp_tx_init(&tx, TESTER_ID, ECU_ID);
    isotp_tx_outcome_t out;
    uint8_t message[8] = {1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u};

    CHECK(isotp_tx_start(&tx, message, 0u, 0u, &out) == ISOTP_ERR_INVALID_ARG,
          "an empty message is rejected");
    CHECK(isotp_tx_start(&tx, NULL, 4u, 0u, &out) == ISOTP_ERR_INVALID_ARG,
          "a null message is rejected");
    CHECK(isotp_tx_start(&tx, message, (uint16_t)(ISOTP_TX_BUFFER_SIZE + 1u), 0u, &out) ==
              ISOTP_ERR_INVALID_ARG,
          "a message larger than the buffer is rejected rather than truncated");
    CHECK(isotp_tx_is_idle(&tx), "none of which leaves the sender busy");
}

/* --------------------------------------------------------------- integration ------ */

/**
 * Drive the sender's frames into the receiver and the receiver's flow control back, which is
 * the real conversation between a tester and an ECU.
 */
static void loopback_once(uint16_t len, uint8_t block_size, uint8_t st_min_raw)
{
    uint8_t message[ISOTP_TX_BUFFER_SIZE];
    fill_pattern(message, len);

    isotp_tx_t tx;
    isotp_rx_t rx;
    isotp_tx_init(&tx, TESTER_ID, ECU_ID);
    /* The receiver listens on what the sender transmits and answers where it listens. */
    isotp_rx_init(&rx, TESTER_ID, ECU_ID, block_size, st_min_raw);

    isotp_tx_outcome_t tout;
    isotp_rx_outcome_t rout;
    uint8_t delivered[ISOTP_RX_BUFFER_SIZE];
    uint16_t delivered_len = 0u;

    uint64_t now = 1000000u;
    CHECK(isotp_tx_start(&tx, message, len, now, &tout) == ISOTP_OK, "the transfer starts");

    bool complete = false;
    int guard = 0;
    while (!complete && guard++ < 20000) {
        if (tout.send_frame) {
            const hal_can_frame_t sent = tout.frame;
            tout.send_frame = false;

            (void)isotp_rx_on_frame(&rx, &sent, now, &rout);
            if (rout.message_complete) {
                delivered_len = rout.message_len;
                memcpy(delivered, rout.message, rout.message_len);
                complete = true;
                break;
            }
            if (rout.send_frame) {
                const hal_can_frame_t flow = rout.frame;
                (void)isotp_tx_on_frame(&tx, &flow, now, &tout);
            }
        }
        now += 200000u; /* past any separation time, well inside both timeouts */
        (void)isotp_tx_on_tick(&tx, now, &tout);
    }

    CHECK(complete, "the loopback transfer completes");
    CHECK(delivered_len == len, "the delivered length matches the submitted one");
    CHECK(delivered_len == len && memcmp(delivered, message, len) == 0,
          "and the payload survives segmentation and reassembly byte for byte");
}

static void test_loopback_across_sizes_and_block_sizes(void)
{
    /* Lengths chosen around the boundaries: single frame, exactly one first frame's worth,
     * the sequence wrap, and the buffer limit. */
    loopback_once(1u, 0u, 0u);
    loopback_once(7u, 0u, 0u);
    loopback_once(8u, 0u, 0u);
    loopback_once(13u, 0u, 0u);
    loopback_once(150u, 0u, 0u);
    loopback_once(150u, 3u, 0x14u);
    loopback_once(511u, 1u, 0xF5u);
    loopback_once((uint16_t)ISOTP_TX_BUFFER_SIZE, 5u, 0x01u);
}

/* -------------------------------------------------------------------- runner ------ */

int isotp_tests_run(void)
{
    s_failures = 0;
    s_checks = 0;

    printf("isotp: rx buffer %u bytes, tx buffer %u bytes\n", (unsigned)ISOTP_RX_BUFFER_SIZE,
           (unsigned)ISOTP_TX_BUFFER_SIZE);

    test_stmin_decoding_covers_every_range();

    test_single_frame_of_every_valid_length();
    test_single_frame_rejections();
    test_foreign_and_flow_control_frames_are_ignored();
    test_multi_frame_reassembly();
    test_sequence_number_wraps_past_fifteen();
    test_padding_is_discarded();
    test_first_frame_rejections();
    test_oversized_transfer_is_refused_and_recovers();
    test_sequence_errors_abandon_the_transfer();
    test_block_size_drives_flow_control();
    test_receiver_timeout();
    test_progress_does_not_time_out();

    test_short_message_sends_as_one_frame();
    test_long_message_waits_for_flow_control();
    test_flow_control_statuses();
    test_block_size_limits_the_burst();
    test_separation_time_is_respected();
    test_sender_timeout();
    test_sender_rejects_bad_arguments();

    test_loopback_across_sizes_and_block_sizes();

    printf("%d checks, %d failures\n", s_checks, s_failures);
    return s_failures;
}
