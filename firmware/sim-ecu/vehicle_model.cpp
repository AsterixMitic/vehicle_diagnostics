/**
 * @file vehicle_model.cpp
 * @brief The drive cycle and the first-order dynamics behind the simulated values.
 */
#include "vehicle_model.h"

const float VehicleModel::IDLE_RPM = 800.0f;
const float VehicleModel::WARM_COOLANT_C = 92.0f;

namespace {

/* The city loop: hold for `seconds` while the driver works towards `kmh`. Sums to 180 s. */
struct Leg {
    uint8_t seconds;
    uint8_t kmh;
};

const Leg CITY_LOOP[] = {
    {10u, 0u},  {12u, 30u}, {8u, 30u},  {10u, 50u}, {15u, 50u}, {8u, 0u},   {12u, 0u},  {10u, 35u}, {10u, 35u},
    {12u, 60u}, {18u, 60u}, {10u, 40u}, {8u, 40u},  {8u, 0u},   {12u, 0u},  {10u, 20u}, {7u, 20u},
};

/* Engine revolutions per km/h in each gear, and the road speed at which the next gear is
 * selected. Gear 0 is idling with the clutch open. */
const float RPM_PER_KMH[6] = {0.0f, 85.0f, 55.0f, 40.0f, 30.0f, 24.0f};
const float UPSHIFT_KMH[5] = {0.5f, 22.0f, 40.0f, 58.0f, 78.0f};

const float ENGINE_LAG_S = 0.5f;       /* engine speed time constant */
const float COOLANT_TAU_S = 150.0f;    /* warm-up towards WARM_COOLANT_C */
const float OIL_TAU_S = 300.0f;        /* oil follows the coolant, slower */
const float MAX_ACCEL_KMH_S = 4.0f;    /* a small petrol engine in town */
const float BRAKE_KMH_S = 8.0f;
const float RPM_WANDER = 25.0f;        /* what the slow engine speed noise is drawn from, min^-1 */
const float LOAD_NOISE_PCT = 1.0f;
const float TANK_L = 40.0f;
const float START_FUEL_PCT = 22.0f;
const float TICK_S = (float)VehicleModel::TICK_MS / 1000.0f;

float clamp(float value, float low, float high)
{
    return value < low ? low : (value > high ? high : value);
}

}  // namespace

VehicleModel::VehicleModel(uint32_t seed)
    : rpm_(IDLE_RPM), rpm_wander_(0.0f), fuel_used_l_(0.0f), pending_ms_(0u), elapsed_ms_(0u),
      rng_(seed == 0u ? 1u : seed)
{
    state_.pedal = 0.0f;
    state_.gear = 0u;
    state_.engine_rpm = IDLE_RPM;
    state_.speed_kmh = 0.0f;
    state_.ambient_c = 20.0f;
    state_.coolant_c = 38.0f;  /* the engine has just been started on a warm day */
    state_.oil_c = 35.0f;
    state_.intake_c = 25.0f;
    state_.baro_kpa = 99.0f;
    state_.intake_kpa = 35.0f;
    state_.engine_load_pct = 15.0f;
    state_.throttle_pct = 27.0f;
    state_.maf_gs = 3.0f;
    state_.fuel_rate_lh = 1.0f;
    state_.fuel_level_pct = START_FUEL_PCT;
    state_.module_voltage_v = 14.0f;
}

float VehicleModel::target_kmh_at(uint32_t cycle_ms) const
{
    uint32_t seconds = cycle_ms / 1000u;
    for (uint8_t i = 0; i < sizeof(CITY_LOOP) / sizeof(CITY_LOOP[0]); i++) {
        if (seconds < CITY_LOOP[i].seconds) {
            return (float)CITY_LOOP[i].kmh;
        }
        seconds -= CITY_LOOP[i].seconds;
    }
    return 0.0f;
}

/** Uniform noise in [-1, 1) from a fixed-seed linear congruential generator. */
float VehicleModel::noise()
{
    rng_ = rng_ * 1664525u + 1013904223u;
    return (float)(rng_ >> 8) / 8388608.0f - 1.0f;
}

void VehicleModel::advance(uint32_t dt_ms)
{
    pending_ms_ += dt_ms;
    while (pending_ms_ >= TICK_MS) {
        pending_ms_ -= TICK_MS;
        elapsed_ms_ += TICK_MS;
        tick();
    }
}

void VehicleModel::tick()
{
    const uint32_t cycle_ms = elapsed_ms_ % CYCLE_MS;
    VehicleState &s = state_;

    /* The driver: accelerate towards the target speed, lift off when above it, brake hard to
     * a stop. The pedal follows from the acceleration asked for. */
    const float target = target_kmh_at(cycle_ms);
    const float error = target - s.speed_kmh;
    float accel = clamp(0.4f * error, -BRAKE_KMH_S, MAX_ACCEL_KMH_S);
    if (target == 0.0f && s.speed_kmh < 4.0f) {
        accel = -3.0f;  /* the last few km/h of a stop are not an exponential tail */
    }
    if (accel > 0.2f) {
        s.pedal = clamp(0.12f + 0.75f * accel / MAX_ACCEL_KMH_S + 0.002f * s.speed_kmh, 0.0f, 1.0f);
    } else if (target > 0.0f && error > -2.0f) {
        s.pedal = 0.08f + 0.002f * s.speed_kmh;  /* holding speed */
    } else {
        s.pedal = 0.0f;
    }
    s.speed_kmh = clamp(s.speed_kmh + accel * TICK_S, 0.0f, 200.0f);

    /* Gear from road speed; idle in neutral when stopped and off the pedal. */
    uint8_t gear = 0u;
    if (s.speed_kmh > UPSHIFT_KMH[0] || s.pedal > 0.0f) {
        gear = 1u;
        while (gear < 5u && s.speed_kmh >= UPSHIFT_KMH[gear]) {
            gear++;
        }
    }
    s.gear = gear;

    /* Engine speed lags its target: road speed through the gear, never below idle. What the
     * ECU reports also wanders a few revolutions around it, slowly, as a real engine does. */
    float target_rpm = IDLE_RPM;
    if (gear > 0u) {
        target_rpm = s.speed_kmh * RPM_PER_KMH[gear];
        if (target_rpm < IDLE_RPM + 250.0f * s.pedal) {
            target_rpm = IDLE_RPM + 250.0f * s.pedal;
        }
    }
    rpm_ += (target_rpm - rpm_) * (TICK_S / ENGINE_LAG_S);
    rpm_wander_ += (RPM_WANDER * noise() - rpm_wander_) * 0.2f;
    s.engine_rpm = clamp(rpm_ + rpm_wander_, 0.0f, 7000.0f);

    /* Pedal-driven quantities. Throttle is the sensor's absolute reading, which sits near 27 %
     * closed. Lifting off in gear closes the throttle and pulls the manifold down (overrun). */
    const bool overrun = gear > 0u && s.pedal == 0.0f && s.speed_kmh > 8.0f;
    s.throttle_pct = 27.0f + 46.0f * s.pedal;
    s.engine_load_pct = (overrun ? 5.0f : 14.0f + 33.0f * s.pedal) + LOAD_NOISE_PCT * noise();
    s.intake_kpa = overrun ? 22.0f : 35.0f + 63.0f * s.pedal;
    s.maf_gs = s.engine_rpm * s.engine_load_pct / 100.0f * 0.0227f + 0.45f;
    s.fuel_rate_lh = overrun ? 0.1f : s.maf_gs * 0.3265f;  /* stoichiometric petrol, 745 g/L */

    /* Temperatures warm asymptotically; intake air follows the engine bay and the load. */
    s.coolant_c += (WARM_COOLANT_C - s.coolant_c) * (TICK_S / COOLANT_TAU_S);
    s.oil_c += (s.coolant_c + 5.0f - s.oil_c) * (TICK_S / OIL_TAU_S);
    s.intake_c = s.ambient_c + 0.25f * (s.coolant_c - s.ambient_c) + 5.0f * s.engine_load_pct / 100.0f;

    /* Slow drifts: the tank empties, the alternator sags a little under load. The fuel used
     * is summed on its own: subtracting each tick's few microlitres from a percentage would
     * be lost in a float's rounding. */
    fuel_used_l_ += s.fuel_rate_lh * TICK_S / 3600.0f;
    s.fuel_level_pct = clamp(START_FUEL_PCT - fuel_used_l_ / TANK_L * 100.0f, 0.0f, 100.0f);
    s.module_voltage_v = 14.0f - 0.4f * s.pedal + 0.05f * noise();
}
