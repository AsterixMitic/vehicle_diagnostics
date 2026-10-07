/**
 * @file vehicle_model.h
 * @brief A small vehicle model that gives the simulated ECU's values a plausible course.
 *
 * A 120 s drive cycle repeats: idle, accelerate through the gears, cruise, brake, idle.
 * Engine speed follows the gear and road speed with a lag, the engine warms from ambient
 * towards about 90 °C, and air flow, load and fuel rate follow the pedal and engine speed.
 * Noise comes from a fixed-seed generator, so every run is the same.
 *
 * Plain C++ with no Arduino dependency, so it builds and tests on a development machine.
 * Everything is float: on the Uno a double is 32 bits too, and the host test must see the
 * same numbers the board produces.
 */
#pragma once

#include <stdint.h>

struct VehicleState {
    float pedal;            /**< 0 to 1. */
    uint8_t gear;           /**< 0 = idle in neutral, 1 to 5. */
    float engine_rpm;
    float speed_kmh;
    float coolant_c;
    float oil_c;
    float intake_c;
    float ambient_c;
    float intake_kpa;       /**< Manifold absolute pressure. */
    float baro_kpa;
    float engine_load_pct;
    float throttle_pct;
    float maf_gs;
    float fuel_rate_lh;
    float fuel_level_pct;
    float module_voltage_v;
};

class VehicleModel {
public:
    /** Drive cycle length. */
    static const uint32_t CYCLE_MS = 120000u;
    static const float IDLE_RPM;
    static const float WARM_COOLANT_C;

    explicit VehicleModel(uint32_t seed = 1u);

    /** Advance the model by @p dt_ms milliseconds. Large steps are split internally. */
    void advance(uint32_t dt_ms);

    const VehicleState &state() const { return state_; }
    uint32_t elapsed_ms() const { return elapsed_ms_; }

private:
    void step(float dt_s);
    float pedal_at(uint32_t cycle_ms) const;
    bool braking_at(uint32_t cycle_ms) const;
    float noise();

    VehicleState state_;
    uint32_t elapsed_ms_;
    uint32_t rng_;
};
