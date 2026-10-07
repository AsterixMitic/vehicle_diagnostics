/**
 * @file obd_decode.c
 * @brief Mode 01 table and frame decoder, step for step as the backend's ObdMode01Decoder.
 */
#include "obd.h"

/* SAE J1979 Mode 01 rows, the same names, units and byte counts as the backend's table. */
static const obd_pid_info_t TABLE[OBD_PID_COUNT] = {
    {0x04u, "engine_load", "percent", 1u},
    {0x05u, "coolant_temp", "degC", 1u},
    {0x0Bu, "intake_pressure", "kPa", 1u},
    {0x0Cu, "engine_rpm", "rpm", 2u},
    {0x0Du, "vehicle_speed", "km_h", 1u},
    {0x0Fu, "intake_temp", "degC", 1u},
    {0x10u, "maf_rate", "g_s", 2u},
    {0x11u, "throttle_position", "percent", 1u},
    {0x2Fu, "fuel_level", "percent", 1u},
    {0x33u, "barometric_pressure", "kPa", 1u},
    {0x42u, "module_voltage", "V", 2u},
    {0x46u, "ambient_temp", "degC", 1u},
    {0x5Cu, "oil_temp", "degC", 1u},
    {0x5Eu, "fuel_rate", "L_h", 2u},
};

const char *obd_ignore_tag(obd_ignore_reason_t reason)
{
    switch (reason) {
    case OBD_IGNORE_NONE:
        return "none";
    case OBD_IGNORE_NOT_OBD_RESPONSE:
        return "not_obd_response";
    case OBD_IGNORE_NOT_SINGLE_FRAME:
        return "not_single_frame";
    case OBD_IGNORE_MULTI_FRAME_UNSUPPORTED:
        return "multi_frame_unsupported";
    case OBD_IGNORE_NEGATIVE_RESPONSE:
        return "negative_response";
    case OBD_IGNORE_UNSUPPORTED_SERVICE:
        return "unsupported_service";
    case OBD_IGNORE_UNSUPPORTED_PID:
        return "unsupported_pid";
    case OBD_IGNORE_SHORT_PAYLOAD:
        return "short_payload";
    case OBD_IGNORE_REASON_COUNT:
    default:
        return "unknown";
    }
}

const obd_pid_info_t *obd_pid_table(void)
{
    return TABLE;
}

const obd_pid_info_t *obd_pid_info(uint8_t pid)
{
    for (size_t i = 0; i < OBD_PID_COUNT; i++) {
        if (TABLE[i].pid == pid) {
            return &TABLE[i];
        }
    }
    return NULL;
}

bool obd_is_response_id(uint32_t id, bool extended)
{
    return extended ? (id & 0xFFFFFF00u) == 0x18DAF100u : (id >= 0x7E8u && id <= 0x7EFu);
}

/** Formula over the data bytes after the PID; A = data[0], B = data[1]. */
static double scale(uint8_t pid, const uint8_t *data)
{
    const double a = (double)data[0];
    switch (pid) {
    case 0x04u:
    case 0x11u:
    case 0x2Fu:
        return a * 100.0 / 255.0;
    case 0x05u:
    case 0x0Fu:
    case 0x46u:
    case 0x5Cu:
        return a - 40.0;
    case 0x0Bu:
    case 0x0Du:
    case 0x33u:
        return a;
    case 0x0Cu:
        return (double)(((unsigned)data[0] << 8) | data[1]) / 4.0;
    case 0x10u:
        return (double)(((unsigned)data[0] << 8) | data[1]) / 100.0;
    case 0x42u:
        return (double)(((unsigned)data[0] << 8) | data[1]) / 1000.0;
    case 0x5Eu:
        return (double)(((unsigned)data[0] << 8) | data[1]) / 20.0;
    default:
        return 0.0;
    }
}

static obd_decode_outcome_t ignored(obd_ignore_reason_t reason)
{
    obd_decode_outcome_t outcome = {0};
    outcome.decoded = false;
    outcome.reason = reason;
    return outcome;
}

/**
 * ISO-TP classification of the first data byte, as the backend's IsoTp.Classify: a single
 * frame declares a length of 1 to 7 that fits the frame; first, consecutive and flow-control
 * frames are multi-frame; anything else is not a valid single frame.
 */
static obd_ignore_reason_t classify(const hal_can_frame_t *frame, uint8_t *length)
{
    if (frame->dlc == 0u) {
        return OBD_IGNORE_NOT_SINGLE_FRAME;
    }

    const uint8_t kind = (uint8_t)(frame->data[0] >> 4);
    if (kind != 0u) {
        return (kind >= 1u && kind <= 3u) ? OBD_IGNORE_MULTI_FRAME_UNSUPPORTED : OBD_IGNORE_NOT_SINGLE_FRAME;
    }

    const uint8_t declared = (uint8_t)(frame->data[0] & 0x0Fu);
    if (declared < 1u || declared > 7u || frame->dlc < (uint8_t)(1u + declared)) {
        return OBD_IGNORE_NOT_SINGLE_FRAME;
    }

    *length = declared;
    return OBD_IGNORE_NONE;
}

obd_decode_outcome_t obd_decode_frame(const hal_can_frame_t *frame)
{
    if (frame == NULL || !obd_is_response_id(frame->id, frame->extended)) {
        return ignored(OBD_IGNORE_NOT_OBD_RESPONSE);
    }

    uint8_t length = 0u;
    const obd_ignore_reason_t kind = classify(frame, &length);
    if (kind != OBD_IGNORE_NONE) {
        return ignored(kind);
    }

    /* Payload without the PCI byte and without padding. */
    const uint8_t *payload = &frame->data[1];
    if (payload[0] == OBD_NEGATIVE_RESPONSE) {
        return ignored(OBD_IGNORE_NEGATIVE_RESPONSE);
    }
    if (payload[0] != OBD_POSITIVE_CURRENT_DATA) {
        return ignored(OBD_IGNORE_UNSUPPORTED_SERVICE);
    }
    if (length < 2u) {
        return ignored(OBD_IGNORE_SHORT_PAYLOAD);
    }

    const obd_pid_info_t *info = obd_pid_info(payload[1]);
    if (info == NULL) {
        return ignored(OBD_IGNORE_UNSUPPORTED_PID);
    }
    if ((uint8_t)(length - 2u) < info->min_bytes) {
        return ignored(OBD_IGNORE_SHORT_PAYLOAD);
    }

    obd_decode_outcome_t outcome = {0};
    outcome.decoded = true;
    outcome.reason = OBD_IGNORE_NONE;
    outcome.reading.pid = info->pid;
    outcome.reading.name = info->name;
    outcome.reading.unit = info->unit;
    outcome.reading.value = scale(info->pid, &payload[2]);
    return outcome;
}
