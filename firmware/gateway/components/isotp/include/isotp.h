/**
 * @file isotp.h
 * @brief ISO 15765-2 transport over classic CAN, normal addressing.
 *
 * Carries diagnostic messages longer than one frame, in both roles: reassembling what an ECU
 * sends and segmenting what the gateway sends.
 *
 * **This layer performs no input or output.** It never transmits, receives, logs, allocates or
 * reads a clock. Every entry point takes the current time as a parameter and returns what the
 * caller should transmit. That is what lets a protocol with two timeouts and a separation timer
 * be tested on a development machine, in microseconds, with no hardware and no scheduler.
 *
 * Scope: classic CAN, normal addressing, lengths up to 4095 bytes. Extended and mixed
 * addressing, CAN FD, and the 2016 escape sequence for longer messages are out of scope; the
 * escape sequence is detected and refused rather than misparsed.
 */
#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "hal_can.h"

#ifdef __cplusplus
extern "C" {
#endif

/** Protocol ceiling for classic CAN without the 2016 escape sequence. */
#define ISOTP_MAX_CLASSIC_PDU 4095u

/**
 * Buffer sizes. Sized for what this project reads rather than for the protocol ceiling: a
 * vehicle identification number is seventeen bytes and a stored-fault-code list rarely exceeds
 * a hundred. A declaration larger than the receive buffer is refused with an overflow flow
 * control, which is exactly what the standard prescribes, so the memory bound and the
 * specified behaviour are the same thing.
 */
#ifndef ISOTP_RX_BUFFER_SIZE
#define ISOTP_RX_BUFFER_SIZE 512u
#endif
#ifndef ISOTP_TX_BUFFER_SIZE
#define ISOTP_TX_BUFFER_SIZE 512u
#endif

/** Payload capacity of each frame type. */
#define ISOTP_SF_MAX_PAYLOAD 7u
#define ISOTP_FF_PAYLOAD 6u
#define ISOTP_CF_MAX_PAYLOAD 7u

/** Frames are padded to full length on transmit; real ECUs commonly expect it. */
#define ISOTP_PADDING_BYTE 0xAAu

/** Standard timeouts: sender awaiting flow control, receiver awaiting a consecutive frame. */
#define ISOTP_N_BS_TIMEOUT_US 1000000u
#define ISOTP_N_CR_TIMEOUT_US 1000000u

/** Longest separation time the encoding can express, and the fallback for reserved values. */
#define ISOTP_STMIN_MAX_US 127000u

/** Protocol control information, the high nibble of the first data byte. */
typedef enum {
    ISOTP_PCI_SINGLE = 0,
    ISOTP_PCI_FIRST = 1,
    ISOTP_PCI_CONSECUTIVE = 2,
    ISOTP_PCI_FLOW_CONTROL = 3,
} isotp_pci_t;

/** Flow status, the low nibble of a flow control frame's first byte. */
typedef enum {
    ISOTP_FS_CONTINUE = 0, /**< Continue to send. */
    ISOTP_FS_WAIT = 1,     /**< Hold; another flow control will follow. */
    ISOTP_FS_OVERFLOW = 2, /**< Receiver cannot hold the declared length. */
} isotp_flow_status_t;

/** Expected failures, every one of them a counter tag. */
typedef enum {
    ISOTP_OK = 0,
    ISOTP_ERR_INVALID_ARG,
    ISOTP_ERR_BUSY,              /**< A transfer is already in progress. */
    ISOTP_ERR_MALFORMED,         /**< Frame violates the encoding. */
    ISOTP_ERR_UNEXPECTED_FRAME,  /**< Correct encoding, wrong moment. */
    ISOTP_ERR_WRONG_SEQUENCE,    /**< Consecutive frame out of order. */
    ISOTP_ERR_OVERFLOW,          /**< Declared length exceeds the buffer. */
    ISOTP_ERR_UNSUPPORTED,       /**< Escape sequence or other out-of-scope encoding. */
    ISOTP_ERR_TIMEOUT_N_CR,      /**< Receiver waited too long for a consecutive frame. */
    ISOTP_ERR_TIMEOUT_N_BS,      /**< Sender waited too long for flow control. */
} isotp_err_t;

/** Stable tag for metrics and logs. */
const char *isotp_err_tag(isotp_err_t error);

/* ------------------------------------------------------------------ receiver ------- */

typedef enum {
    ISOTP_RX_IDLE = 0,
    ISOTP_RX_ASSEMBLING,
} isotp_rx_state_t;

typedef struct {
    isotp_rx_state_t state;
    uint8_t buffer[ISOTP_RX_BUFFER_SIZE];
    uint16_t expected_len;
    uint16_t received_len;
    uint8_t next_sn;          /**< Sequence number the next consecutive frame must carry. */
    uint16_t block_remaining; /**< Consecutive frames still allowed before the next flow control. */

    uint32_t rx_can_id;       /**< Identifier this receiver reassembles from. */
    uint32_t fc_can_id;       /**< Identifier its flow control is sent on. */
    uint8_t block_size;       /**< Advertised block size; zero means the whole message. */
    uint8_t st_min_raw;       /**< Advertised separation time, in wire encoding. */

    uint64_t last_frame_us;

    uint32_t messages_completed;
    uint32_t errors;
} isotp_rx_t;

/** What the caller must do after feeding the receiver a frame or a tick. */
typedef struct {
    bool send_frame;            /**< Transmit @c frame. */
    hal_can_frame_t frame;
    bool message_complete;      /**< @c message and @c message_len are valid. */
    const uint8_t *message;     /**< Points into the receiver's buffer; valid until the next call. */
    uint16_t message_len;
    bool error;
    isotp_err_t error_code;
} isotp_rx_outcome_t;

/**
 * @param rx_can_id  identifier the peer sends on
 * @param fc_can_id  identifier this receiver sends flow control on
 * @param block_size consecutive frames to allow per flow control; zero means all of them
 * @param st_min_raw separation time to request, in wire encoding
 */
void isotp_rx_init(isotp_rx_t *rx, uint32_t rx_can_id, uint32_t fc_can_id, uint8_t block_size,
                   uint8_t st_min_raw);

/** Abandon any transfer in progress and return to idle. Counters are preserved. */
void isotp_rx_reset(isotp_rx_t *rx);

/**
 * Feed one received frame.
 * A frame on another identifier, or a flow control frame, is ignored: the outcome is empty and
 * the return is @c ISOTP_OK, because on a shared bus most traffic belongs to someone else.
 */
isotp_err_t isotp_rx_on_frame(isotp_rx_t *rx, const hal_can_frame_t *frame, uint64_t now_us,
                              isotp_rx_outcome_t *out);

/** Advance time. Reports the receiver timeout when a transfer has stalled. */
isotp_err_t isotp_rx_on_tick(isotp_rx_t *rx, uint64_t now_us, isotp_rx_outcome_t *out);

bool isotp_rx_is_idle(const isotp_rx_t *rx);

/* -------------------------------------------------------------------- sender ------- */

typedef enum {
    ISOTP_TX_IDLE = 0,
    ISOTP_TX_WAIT_FC,  /**< First frame or block sent; awaiting flow control. */
    ISOTP_TX_SENDING,  /**< Permitted to emit consecutive frames. */
} isotp_tx_state_t;

typedef struct {
    isotp_tx_state_t state;
    uint8_t buffer[ISOTP_TX_BUFFER_SIZE];
    uint16_t total_len;
    uint16_t sent_len;
    uint8_t next_sn;

    uint8_t block_size;       /**< Granted by the peer; zero means no further flow control. */
    uint16_t block_remaining;
    uint32_t st_min_us;       /**< Granted by the peer, normalised to microseconds. */
    uint64_t next_cf_due_us;  /**< Earliest time the next consecutive frame may be sent. */

    uint32_t tx_can_id;       /**< Identifier this sender transmits on. */
    uint32_t fc_can_id;       /**< Identifier the peer's flow control arrives on. */

    uint64_t last_event_us;

    uint32_t messages_sent;
    uint32_t errors;
} isotp_tx_t;

typedef struct {
    bool send_frame;
    hal_can_frame_t frame;
    bool message_sent;   /**< Every frame of the message has now been emitted. */
    bool error;
    isotp_err_t error_code;
} isotp_tx_outcome_t;

/**
 * @param tx_can_id identifier to transmit on
 * @param fc_can_id identifier the peer's flow control arrives on
 */
void isotp_tx_init(isotp_tx_t *tx, uint32_t tx_can_id, uint32_t fc_can_id);

void isotp_tx_reset(isotp_tx_t *tx);

/**
 * Begin sending a message. Produces a single frame for a short message, or a first frame after
 * which the sender waits for flow control.
 */
isotp_err_t isotp_tx_start(isotp_tx_t *tx, const uint8_t *data, uint16_t len, uint64_t now_us,
                           isotp_tx_outcome_t *out);

/** Feed one received frame. Only flow control on the expected identifier is acted on. */
isotp_err_t isotp_tx_on_frame(isotp_tx_t *tx, const hal_can_frame_t *frame, uint64_t now_us,
                              isotp_tx_outcome_t *out);

/**
 * Advance time. Emits the next consecutive frame once the separation time has elapsed, or
 * reports the sender timeout when flow control never arrived. Emits at most one frame per call.
 */
isotp_err_t isotp_tx_on_tick(isotp_tx_t *tx, uint64_t now_us, isotp_tx_outcome_t *out);

bool isotp_tx_is_idle(const isotp_tx_t *tx);

/* -------------------------------------------------------------------- codec -------- */

/**
 * Decode a separation time byte to microseconds. Values up to 0x7F are milliseconds, 0xF1 to
 * 0xF9 are 100 to 900 microseconds, and every other value falls back to the defined maximum.
 * Treating a reserved value as zero would flood a peer that asked to be slowed down.
 */
uint32_t isotp_decode_stmin_us(uint8_t raw);

#ifdef __cplusplus
}
#endif
