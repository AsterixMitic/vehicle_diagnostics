/**
 * @file obd_responder.cpp
 * @brief Mode 01 encoding with the inverse SAE J1979 formulas, and the support bitmaps.
 */
#include "obd_responder.h"

namespace obd_sim {

namespace {

const uint8_t PADDING = 0x55u;
const uint8_t SERVICE_CURRENT_DATA = 0x01u;
const uint8_t POSITIVE_CURRENT_DATA = 0x41u;

/* The 14 PIDs the backend and the gateway decode, plus the next-range support PIDs. */
const uint8_t SUPPORTED[] = {0x04u, 0x05u, 0x0Bu, 0x0Cu, 0x0Du, 0x0Fu, 0x10u, 0x11u,
                             0x20u, 0x2Fu, 0x33u, 0x40u, 0x42u, 0x46u, 0x5Cu, 0x5Eu};

float clamp(float value, float low, float high)
{
    return value < low ? low : (value > high ? high : value);
}

uint16_t rounded(float value)
{
    return (uint16_t)(value + 0.5f);
}

}  // namespace

bool supports(uint8_t pid)
{
    for (uint8_t i = 0; i < sizeof(SUPPORTED); i++) {
        if (SUPPORTED[i] == pid) {
            return true;
        }
    }
    return false;
}

bool model_value(const VehicleState &s, uint8_t pid, float *value)
{
    float v;
    switch (pid) {
    case 0x04u: v = clamp(s.engine_load_pct, 0.0f, 100.0f); break;
    case 0x05u: v = clamp(s.coolant_c, -40.0f, 215.0f); break;
    case 0x0Bu: v = clamp(s.intake_kpa, 0.0f, 255.0f); break;
    case 0x0Cu: v = clamp(s.engine_rpm, 0.0f, 16383.75f); break;
    case 0x0Du: v = clamp(s.speed_kmh, 0.0f, 255.0f); break;
    case 0x0Fu: v = clamp(s.intake_c, -40.0f, 215.0f); break;
    case 0x10u: v = clamp(s.maf_gs, 0.0f, 655.35f); break;
    case 0x11u: v = clamp(s.throttle_pct, 0.0f, 100.0f); break;
    case 0x2Fu: v = clamp(s.fuel_level_pct, 0.0f, 100.0f); break;
    case 0x33u: v = clamp(s.baro_kpa, 0.0f, 255.0f); break;
    case 0x42u: v = clamp(s.module_voltage_v, 0.0f, 65.535f); break;
    case 0x46u: v = clamp(s.ambient_c, -40.0f, 215.0f); break;
    case 0x5Cu: v = clamp(s.oil_c, -40.0f, 210.0f); break;
    case 0x5Eu: v = clamp(s.fuel_rate_lh, 0.0f, 3276.75f); break;
    default: return false;
    }
    *value = v;
    return true;
}

namespace {

/** Encode @p pid's value into data bytes A (and B); returns the number of bytes. */
uint8_t encode(const VehicleState &s, uint8_t pid, uint8_t *out)
{
    float v = 0.0f;
    if (!model_value(s, pid, &v)) {
        return 0u;
    }
    uint16_t raw;
    switch (pid) {
    case 0x04u:
    case 0x11u:
    case 0x2Fu:
        out[0] = (uint8_t)rounded(v * 255.0f / 100.0f);
        return 1u;
    case 0x05u:
    case 0x0Fu:
    case 0x46u:
    case 0x5Cu:
        out[0] = (uint8_t)rounded(v + 40.0f);
        return 1u;
    case 0x0Bu:
    case 0x0Du:
    case 0x33u:
        out[0] = (uint8_t)rounded(v);
        return 1u;
    case 0x0Cu: raw = rounded(v * 4.0f); break;
    case 0x10u: raw = rounded(v * 100.0f); break;
    case 0x42u: raw = rounded(v * 1000.0f); break;
    case 0x5Eu: raw = rounded(v * 20.0f); break;
    default: return 0u;
    }
    out[0] = (uint8_t)(raw >> 8);
    out[1] = (uint8_t)(raw & 0xFFu);
    return 2u;
}

/** Bitmap for support PID @p range: bit 7 of A declares range+1, bit 0 of D range+0x20. */
void bitmap(uint8_t range, uint8_t *out)
{
    for (uint8_t i = 0; i < 4u; i++) {
        out[i] = 0u;
    }
    for (uint8_t bit = 0; bit < 32u; bit++) {
        const uint8_t pid = (uint8_t)(range + 1u + bit);
        if (supports(pid)) {
            out[bit / 8u] = (uint8_t)(out[bit / 8u] | (0x80u >> (bit % 8u)));
        }
    }
}

}  // namespace

bool respond(const VehicleState &state, uint32_t id, const uint8_t *data, uint8_t dlc, Frame *response)
{
    if (data == 0 || response == 0 || dlc < 3u) {
        return false;
    }
    if (id != FUNCTIONAL_REQUEST_ID && id != PHYSICAL_REQUEST_ID) {
        return false;
    }
    /* A single frame of at least two bytes asking service 01. */
    if ((data[0] >> 4) != 0u || (data[0] & 0x0Fu) < 2u || data[1] != SERVICE_CURRENT_DATA) {
        return false;
    }

    const uint8_t pid = data[2];
    uint8_t value[4];
    uint8_t length;
    if (pid == 0x00u || pid == 0x20u || pid == 0x40u) {
        if (pid != 0x00u && !supports(pid)) {
            return false;
        }
        bitmap(pid, value);
        length = 4u;
    } else {
        if (!supports(pid)) {
            return false;  /* silent, as a real ECU on a functional request */
        }
        length = encode(state, pid, value);
        if (length == 0u) {
            return false;
        }
    }

    response->id = RESPONSE_ID;
    response->dlc = 8u;
    response->data[0] = (uint8_t)(2u + length);
    response->data[1] = POSITIVE_CURRENT_DATA;
    response->data[2] = pid;
    for (uint8_t i = 0; i < 5u; i++) {
        response->data[3 + i] = i < length ? value[i] : PADDING;
    }
    return true;
}

}  // namespace obd_sim
