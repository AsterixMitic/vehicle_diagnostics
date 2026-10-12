/**
 * @file obd_sched.c
 * @brief Mode 01 discovery and polling as an explicit state machine with time as an argument.
 */
#include "obd_sched.h"

#include <string.h>

/** ISO 15765-4 recommends padding requests to eight bytes. */
#define REQUEST_PADDING 0x55u
#define LAST_SUPPORT_PID 0x40u

obd_sched_config_t obd_sched_default_config(void)
{
    obd_sched_config_t config = {
        .request_id = OBD_ENGINE_REQUEST_ID,
        .response_id = OBD_ENGINE_RESPONSE_ID,
        .period_us = 1000000u,
        .timeout_us = OBD_RESPONSE_TIMEOUT_US,
        .discovery_attempts = 3u,
        .discovery_retry_us = OBD_DISCOVERY_RETRY_US,
    };
    return config;
}

static void clear(obd_sched_outcome_t *out)
{
    memset(out, 0, sizeof(*out));
    out->event = OBD_EVENT_NONE;
    out->reason = OBD_IGNORE_NONE;
}

/** The only frame this component ever asks to transmit: a Mode 01 request for one PID. */
static void request(obd_sched_t *sched, uint8_t pid, uint64_t now_us, obd_sched_outcome_t *out)
{
    out->send_frame = true;
    out->frame.id = sched->config.request_id;
    out->frame.extended = sched->config.extended;
    out->frame.dlc = 8u;
    out->frame.data[0] = 0x02u;  /* single frame, two bytes */
    out->frame.data[1] = OBD_SERVICE_CURRENT_DATA;
    out->frame.data[2] = pid;
    for (int i = 3; i < 8; i++) {
        out->frame.data[i] = REQUEST_PADDING;
    }
    out->frame.timestamp_us = now_us;
    sched->deadline_us = now_us + sched->config.timeout_us;
    sched->counters.requests++;
}

void obd_sched_init(obd_sched_t *sched, const obd_sched_config_t *config)
{
    if (sched == NULL) {
        return;
    }
    memset(sched, 0, sizeof(*sched));
    sched->config = config != NULL ? *config : obd_sched_default_config();
    if (sched->config.discovery_attempts == 0u) {
        sched->config.discovery_attempts = 1u;
    }
    obd_support_init(&sched->support);
    sched->range_pid = 0x00u;
    sched->state = OBD_SCHED_DISCOVER_SEND;
}

/**
 * Nothing to poll: the ECU is silent (not powered yet, ignition off) or declares none of the
 * table's PIDs. Start over later, so the order in which the two ends are switched on does not
 * matter.
 */
static void fail_discovery(obd_sched_t *sched, uint64_t now_us, obd_sched_outcome_t *out)
{
    sched->state = OBD_SCHED_DISCOVERY_FAILED;
    sched->retry_at_us = now_us + sched->config.discovery_retry_us;
    out->event = OBD_EVENT_DISCOVERY_FAILED;
}

static void send_discovery(obd_sched_t *sched, uint64_t now_us, obd_sched_outcome_t *out)
{
    sched->attempts++;
    request(sched, sched->range_pid, now_us, out);
    sched->state = OBD_SCHED_DISCOVER_WAIT;
}

static void start_cycle(obd_sched_t *sched, uint64_t start_us, uint64_t now_us, obd_sched_outcome_t *out)
{
    sched->cycle_start_us = start_us;
    sched->poll_index = 0u;
    sched->counters.cycles++;
    request(sched, sched->poll_pids[0], now_us, out);
    sched->state = OBD_SCHED_POLL_WAIT;
}

/** Discovery is over: poll the table's PIDs the ECU declared, starting a cycle right away. */
static void finish_discovery(obd_sched_t *sched, uint64_t now_us, obd_sched_outcome_t *out)
{
    const obd_pid_info_t *table = obd_pid_table();
    sched->poll_count = 0u;
    for (size_t i = 0; i < OBD_PID_COUNT; i++) {
        if (obd_support_has(&sched->support, table[i].pid)) {
            sched->poll_pids[sched->poll_count++] = table[i].pid;
        }
    }

    if (sched->poll_count == 0u) {
        fail_discovery(sched, now_us, out);
        return;
    }
    out->event = OBD_EVENT_DISCOVERED;
    start_cycle(sched, now_us, now_us, out);
}

/** The awaited PID is settled (answered, refused or timed out); go on with the cycle. */
static void advance(obd_sched_t *sched, uint64_t now_us, obd_sched_outcome_t *out)
{
    sched->poll_index++;
    if (sched->poll_index < sched->poll_count) {
        request(sched, sched->poll_pids[sched->poll_index], now_us, out);
        return;
    }
    sched->state = OBD_SCHED_CYCLE_WAIT;
}

void obd_sched_on_tick(obd_sched_t *sched, uint64_t now_us, obd_sched_outcome_t *out)
{
    if (sched == NULL || out == NULL) {
        return;
    }
    clear(out);

    switch (sched->state) {
    case OBD_SCHED_DISCOVER_SEND:
        send_discovery(sched, now_us, out);
        break;

    case OBD_SCHED_DISCOVER_WAIT:
        if (now_us < sched->deadline_us) {
            break;
        }
        sched->counters.timeouts++;
        out->pid = sched->range_pid;
        if (sched->attempts < sched->config.discovery_attempts) {
            out->event = OBD_EVENT_TIMEOUT;
            send_discovery(sched, now_us, out);
        } else if (sched->range_pid == 0x00u) {
            /* Not even the first bitmap: the ECU declares nothing, so nothing is polled. */
            fail_discovery(sched, now_us, out);
        } else {
            /* A later range stays silent: poll what the earlier ranges declared. */
            finish_discovery(sched, now_us, out);
        }
        break;

    case OBD_SCHED_POLL_WAIT:
        if (now_us < sched->deadline_us) {
            break;
        }
        sched->counters.timeouts++;
        out->event = OBD_EVENT_TIMEOUT;
        out->pid = sched->poll_pids[sched->poll_index];
        advance(sched, now_us, out);
        break;

    case OBD_SCHED_CYCLE_WAIT: {
        const uint64_t next = sched->cycle_start_us + sched->config.period_us;
        if (now_us < next) {
            break;
        }
        /* Keep the period from the previous start; after a long stall, restart from now
         * instead of firing a burst of cycles to catch up. */
        const uint64_t start = (now_us - next) < sched->config.period_us ? next : now_us;
        start_cycle(sched, start, now_us, out);
        break;
    }

    case OBD_SCHED_DISCOVERY_FAILED:
        if (sched->config.discovery_retry_us == 0u || now_us < sched->retry_at_us) {
            break;
        }
        obd_support_init(&sched->support);
        sched->range_pid = 0x00u;
        sched->attempts = 0u;
        send_discovery(sched, now_us, out);
        break;

    case OBD_SCHED_STOPPED:
    default:
        break;
    }
}

/** A frame that is not the awaited answer: classify and count it. */
static void not_awaited(obd_sched_t *sched, const hal_can_frame_t *frame, obd_sched_outcome_t *out)
{
    const obd_decode_outcome_t decoded = obd_decode_frame(frame);
    out->source_id = frame->id;
    if (!decoded.decoded) {
        sched->counters.ignored[decoded.reason]++;
        out->event = OBD_EVENT_IGNORED;
        out->reason = decoded.reason;
        return;
    }
    out->pid = decoded.reading.pid;
    if (frame->extended != sched->config.extended || frame->id != sched->config.response_id) {
        sched->counters.other_ecu++;
        out->event = OBD_EVENT_OTHER_ECU;
    } else {
        sched->counters.unexpected++;
        out->event = OBD_EVENT_UNEXPECTED;
    }
}

static bool from_engine(const obd_sched_t *sched, const hal_can_frame_t *frame)
{
    return frame->extended == sched->config.extended && frame->id == sched->config.response_id;
}

/** A negative answer to the awaited request: 7F, the requested service, a response code. */
static bool refuses(const hal_can_frame_t *frame)
{
    return frame->dlc >= 4u && (frame->data[0] >> 4) == 0u && (frame->data[0] & 0x0Fu) >= 3u &&
           frame->data[1] == OBD_NEGATIVE_RESPONSE && frame->data[2] == OBD_SERVICE_CURRENT_DATA;
}

void obd_sched_on_frame(obd_sched_t *sched, const hal_can_frame_t *frame, uint64_t now_us,
                        obd_sched_outcome_t *out)
{
    if (sched == NULL || frame == NULL || out == NULL) {
        return;
    }
    clear(out);

    if (sched->state == OBD_SCHED_DISCOVER_WAIT && from_engine(sched, frame)) {
        uint8_t range = 0u;
        uint8_t data[4];
        if (obd_parse_support_response(frame, &range, data) && range == sched->range_pid) {
            sched->counters.answers++;
            out->source_id = frame->id;
            (void)obd_support_apply(&sched->support, range, data);
            if (obd_support_declares_next(&sched->support, range) && range < LAST_SUPPORT_PID) {
                sched->range_pid = (uint8_t)(range + 0x20u);
                sched->attempts = 0u;
                send_discovery(sched, now_us, out);
            } else {
                finish_discovery(sched, now_us, out);
            }
            return;
        }
    }

    if (sched->state == OBD_SCHED_POLL_WAIT && from_engine(sched, frame)) {
        const uint8_t awaited = sched->poll_pids[sched->poll_index];
        const obd_decode_outcome_t decoded = obd_decode_frame(frame);
        if (decoded.decoded && decoded.reading.pid == awaited) {
            sched->counters.answers++;
            out->event = OBD_EVENT_READING;
            out->pid = awaited;
            out->reading = decoded.reading;
            out->source_id = frame->id;
            advance(sched, now_us, out);
            return;
        }
        if (refuses(frame)) {
            sched->counters.negative++;
            out->event = OBD_EVENT_NEGATIVE_RESPONSE;
            out->pid = awaited;
            out->source_id = frame->id;
            advance(sched, now_us, out);
            return;
        }
    }

    not_awaited(sched, frame, out);
}

void obd_sched_stop(obd_sched_t *sched)
{
    if (sched != NULL) {
        sched->state = OBD_SCHED_STOPPED;
    }
}

bool obd_sched_polls(const obd_sched_t *sched, uint8_t pid)
{
    if (sched == NULL) {
        return false;
    }
    for (uint8_t i = 0; i < sched->poll_count; i++) {
        if (sched->poll_pids[i] == pid) {
            return true;
        }
    }
    return false;
}
