/**
 * @file vehicle_model.cpp
 * @brief The drive cycle and the first-order dynamics behind the simulated values.
 */
#include "vehicle_model.h"

const float VehicleModel::IDLE_RPM = 800.0f;
const float VehicleModel::WARM_COOLANT_C = 90.0f;

namespace {

/* Drive cycle phases, in milliseconds from the start of the cycle. */
const uint32_t ACCELERATE_FROM_MS = 15000u;
const uint32_t CRUISE_FROM_MS = 35000u;
const uint32_t BRAKE_FROM_MS = 75000u;
const uint32_t IDLE_AGAIN_FROM_MS = 95000u;

const float ACCELERATE_PEDAL = 0.6f;
const float CRUISE_PEDAL = 0.25f;

/* Engine revolutions per km/h in each gear, and the road speed at which the next gear is
 * selected. Gear 0 is idling with the clutch open. */
const float RPM_PER_KMH[6] = {0.0f, 130.0f, 70.0f, 45.0f, 34.0f, 27.0f};
const float UPSHIFT_KMH[5] = {0.5f, 20.0f, 40.0f, 60.0f, 80.0f};

const float ENGINE_LAG_S = 0.6f;       /* engine speed time constant */
const float COOLANT_TAU_S = 120.0f;    /* warm-up from ambient towards WARM_COOLANT_C */
const float OIL_TAU_S = 300.0f;        /* oil follows the coolant, slower */
const float MAX_ACCEL_KMH_S = 10.0f;   /* about 2.8 m/s^2 at full pedal */
const float BRAKE_KMH_S = 9.0f;        /* about 2.5 m/s^2 */
const float DRAG_PER_KMH2 = 0.0005f;   /* cruise near 66 km/h at the cruise pedal */
const float TANK_L = 50.0f;

float clamp(float value, float low, float high)
{
    return value < low ? low : (value > high ? high : value);
}

}  // namespace

VehicleModel::VehicleModel(uint32_t seed) : elapsed_ms_(0u), rng_(seed == 0u ? 1u : seed)
{
    state_.pedal = 0.0f;
    state_.gear = 0u;
    state_.engine_rpm = IDLE_RPM;
    state_.speed_kmh = 0.0f;
    state_.ambient_c = 22.0f;
    state_.coolant_c = state_.ambient_c;
    state_.oil_c = state_.ambient_c;
    state_.intake_c = state_.ambient_c + 5.0f;
    state_.baro_kpa = 101.0f;
    state_.intake_kpa = 30.0f;
    state_.engine_load_pct = 20.0f;
    state_.throttle_pct = 15.0f;
    state_.maf_gs = 2.4f;
    state_.fuel_rate_lh = 0.8f;
    state_.fuel_level_pct = 62.0f;
    state_.module_voltage_v = 13.8f;
}

float VehicleModel::pedal_at(uint32_t cycle_ms) const
{
    if (cycle_ms >= ACCELERATE_FROM_MS && cycle_ms < CRUISE_FROM_MS) {
        return ACCELERATE_PEDAL;
    }
    if (cycle_ms >= CRUISE_FROM_MS && cycle_ms < BRAKE_FROM_MS) {
        return CRUISE_PEDAL;
    }
    return 0.0f;
}

bool VehicleModel::braking_at(uint32_t cycle_ms) const
{
    return cycle_ms >= BRAKE_FROM_MS && cycle_ms < IDLE_AGAIN_FROM_MS;
}

/** Uniform noise in [-1, 1) from a fixed-seed linear congruential generator. */
float VehicleModel::noise()
{
    rng_ = rng_ * 1664525u + 1013904223u;
    return (float)(rng_ >> 8) / 8388608.0f - 1.0f;
}

void VehicleModel::advance(uint32_t dt_ms)
{
    /* Split long gaps so the first-order dynamics stay stable. */
    while (dt_ms > 0u) {
        const uint32_t chunk = dt_ms > 100u ? 100u : dt_ms;
        elapsed_ms_ += chunk;
        step((float)chunk / 1000.0f);
        dt_ms -= chunk;
    }
}

void VehicleModel::step(float dt_s)
{
    const uint32_t cycle_ms = elapsed_ms_ % CYCLE_MS;
    VehicleState &s = state_;
    s.pedal = pedal_at(cycle_ms);

    /* Road speed: the pedal accelerates against drag, the brake decelerates. */
    float accel = s.pedal * MAX_ACCEL_KMH_S - DRAG_PER_KMH2 * s.speed_kmh * s.speed_kmh - 0.3f;
    if (braking_at(cycle_ms)) {
        accel = -BRAKE_KMH_S;
    }
    if (s.pedal == 0.0f && !braking_at(cycle_ms)) {
        accel = -1.5f;  /* rolling to a stop at idle */
    }
    s.speed_kmh = clamp(s.speed_kmh + accel * dt_s, 0.0f, 200.0f);

    /* Gear from road speed; idle in neutral when stopped and off the pedal. */
    uint8_t gear = 0u;
    if (s.speed_kmh > UPSHIFT_KMH[0] || s.pedal > 0.0f) {
        gear = 1u;
        while (gear < 5u && s.speed_kmh >= UPSHIFT_KMH[gear]) {
            gear++;
        }
    }
    s.gear = gear;

    /* Engine speed lags its target: road speed through the gear, never below idle. */
    float target_rpm = IDLE_RPM;
    if (gear > 0u) {
        target_rpm = s.speed_kmh * RPM_PER_KMH[gear];
        if (target_rpm < IDLE_RPM + 300.0f * s.pedal) {
            target_rpm = IDLE_RPM + 300.0f * s.pedal;
        }
    }
    s.engine_rpm += (target_rpm - s.engine_rpm) * (dt_s / ENGINE_LAG_S > 1.0f ? 1.0f : dt_s / ENGINE_LAG_S);
    s.engine_rpm = clamp(s.engine_rpm, 0.0f, 7000.0f);

    /* Pedal-driven quantities. */
    s.throttle_pct = 15.0f + 85.0f * s.pedal;
    s.engine_load_pct = 20.0f + 70.0f * s.pedal;
    s.intake_kpa = 30.0f + 70.0f * s.pedal;
    s.maf_gs = s.engine_rpm * s.engine_load_pct / 100.0f * 0.015f;
    s.fuel_rate_lh = s.maf_gs * 0.3265f;  /* stoichiometric petrol, 750 g/L */

    /* Temperatures warm asymptotically; intake air follows load. */
    s.coolant_c += (WARM_COOLANT_C - s.coolant_c) * (dt_s / COOLANT_TAU_S);
    s.oil_c += (s.coolant_c + 5.0f - s.oil_c) * (dt_s / OIL_TAU_S);
    s.intake_c = s.ambient_c + 5.0f + 10.0f * s.engine_load_pct / 100.0f;

    /* Slow drifts. */
    s.fuel_level_pct = clamp(s.fuel_level_pct - s.fuel_rate_lh * dt_s / 3600.0f / TANK_L * 100.0f, 0.0f, 100.0f);
    s.module_voltage_v = 13.8f + 0.05f * noise();
}
