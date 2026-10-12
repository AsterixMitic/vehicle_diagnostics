/**
 * @file vehicle_model.h
 * @brief A small vehicle model that gives the simulated ECU's values a plausible course.
 *
 * A 180 s city loop repeats: pull away, cruise at 30, 50 and 60 km/h, brake to a stop at
 * two lights, idle. The driver is a speed controller that follows a short table of target
 * speeds, so the pedal, the gear, engine speed, manifold pressure, air flow, load, fuel rate
 * and voltage all move together the way a car's do. The constants are calibrated on a drive
 * recorded on a 2015 petrol hatchback, but none of its samples are stored: the model is a
 * table of seventeen legs and a handful of formulas instead of a recording in flash.
 *
 * Engine speed follows the gear and road speed with a lag, the engine warms from a warm
 * start towards about 92 °C, and lifting off in gear cuts the fuel and pulls the manifold
 * down. Engine speed and load carry a little slow noise, as a real idle does. Noise comes
 * from a fixed-seed generator and the model advances in fixed 100 ms ticks, so a run does not
 * depend on how often the model is called: the Uno's loop() and the host tests walk through
 * the same sequence of steps.
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
    static const uint32_t CYCLE_MS = 180000u;
    static const float IDLE_RPM;
    static const float WARM_COOLANT_C;

    explicit VehicleModel(uint32_t seed = 1u);

    /** One integration step; the model's values change only on these ticks. */
    static const uint32_t TICK_MS = 100u;

    /**
     * Let @p dt_ms milliseconds pass. The model moves in whole ticks, so calling this every
     * millisecond or once a second gives the same values at the same elapsed time.
     */
    void advance(uint32_t dt_ms);

    const VehicleState &state() const { return state_; }
    uint32_t elapsed_ms() const { return elapsed_ms_; }

private:
    void tick();
    float target_kmh_at(uint32_t cycle_ms) const;
    float noise();

    VehicleState state_;
    float rpm_;          /**< Engine speed without the idle wander. */
    float rpm_wander_;   /**< Slow noise added to the reported engine speed. */
    float fuel_used_l_;
    uint32_t pending_ms_;
    uint32_t elapsed_ms_;
    uint32_t rng_;
};
