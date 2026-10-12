/**
 * @file test_sim.cpp
 * @brief Host tests for the simulated ECU: Mode 01 answers, support bitmaps, the vehicle
 *        model's dynamics, the replay of the recorded drive, and the round trip through the
 *        gateway's decoder.
 */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "drive_profile.h"
#include "drive_replay.h"
#include "obd.h"
#include "obd_responder.h"
#include "vehicle_model.h"

#ifndef PROFILE_CSV
#define PROFILE_CSV "../../../../experiments/profiles/asx-2012.csv"
#endif

static const int DRIVE_PID_COUNT_EXPECTED = 10;

static int s_failures;
static int s_checks;

#define CHECK(condition, description)                                            \
    do {                                                                         \
        s_checks++;                                                              \
        if (!(condition)) {                                                      \
            s_failures++;                                                        \
            printf("  FAIL %s:%d  %s\n", __func__, __LINE__, (description));     \
        }                                                                        \
    } while (0)

static bool ask(const VehicleState &state, uint32_t id, uint8_t pid, obd_sim::Frame *answer)
{
    const uint8_t request[8] = {0x02u, 0x01u, pid, 0x55u, 0x55u, 0x55u, 0x55u, 0x55u};
    return obd_sim::respond(state, id, request, 8u, answer);
}

static bool ask_replay(uint32_t elapsed_ms, uint8_t pid, obd_sim::Frame *answer)
{
    const uint8_t request[8] = {0x02u, 0x01u, pid, 0x55u, 0x55u, 0x55u, 0x55u, 0x55u};
    return obd_sim::respond(obd_sim::ReplaySource(elapsed_ms), obd_sim::PHYSICAL_REQUEST_ID, request, 8u, answer);
}

static hal_can_frame_t as_can(const obd_sim::Frame &frame)
{
    hal_can_frame_t out;
    memset(&out, 0, sizeof(out));
    out.id = frame.id;
    out.extended = false;
    out.dlc = frame.dlc;
    memcpy(out.data, frame.data, frame.dlc);
    return out;
}

static void test_speed_is_encoded_as_in_the_standard()
{
    VehicleState state = VehicleModel().state();
    state.speed_kmh = 60.0f;
    obd_sim::Frame answer;
    CHECK(ask(state, obd_sim::PHYSICAL_REQUEST_ID, 0x0Du, &answer), "vehicle speed is answered");
    const uint8_t expected[8] = {0x03u, 0x41u, 0x0Du, 0x3Cu, 0x55u, 0x55u, 0x55u, 0x55u};
    CHECK(answer.id == obd_sim::RESPONSE_ID && answer.dlc == 8u && memcmp(answer.data, expected, 8) == 0,
          "60 km/h is 03 41 0D 3C padded with 0x55, on 0x7E8");
}

static void test_functional_and_physical_requests_are_answered()
{
    const VehicleState state = VehicleModel().state();
    obd_sim::Frame answer;
    CHECK(ask(state, obd_sim::FUNCTIONAL_REQUEST_ID, 0x0Cu, &answer), "a functional request is answered");
    CHECK(ask(state, obd_sim::PHYSICAL_REQUEST_ID, 0x0Cu, &answer), "a physical request is answered");
    CHECK(!ask(state, 0x7E1u, 0x0Cu, &answer), "another ECU's physical identifier is not this ECU");

    const uint8_t vin_request[8] = {0x02u, 0x09u, 0x02u, 0x55u, 0x55u, 0x55u, 0x55u, 0x55u};
    CHECK(!obd_sim::respond(state, obd_sim::PHYSICAL_REQUEST_ID, vin_request, 8u, &answer),
          "a service other than Mode 01 gets no answer");
}

static void test_unsupported_pid_is_silent()
{
    const VehicleState state = VehicleModel().state();
    obd_sim::Frame answer;
    CHECK(!ask(state, obd_sim::PHYSICAL_REQUEST_ID, 0x0Au, &answer), "fuel pressure is not supported: silence");
    CHECK(!ask(state, obd_sim::PHYSICAL_REQUEST_ID, 0x60u, &answer), "support range 0x60 is not declared: silence");
}

static void test_bitmaps_match_what_is_answered()
{
    const VehicleState state = VehicleModel().state();
    obd_support_t support;
    obd_support_init(&support);
    const uint8_t ranges[3] = {0x00u, 0x20u, 0x40u};
    for (int r = 0; r < 3; r++) {
        obd_sim::Frame answer;
        CHECK(ask(state, obd_sim::PHYSICAL_REQUEST_ID, ranges[r], &answer), "every declared range is answered");
        const hal_can_frame_t frame = as_can(answer);
        uint8_t range = 0xFFu;
        uint8_t data[4];
        CHECK(obd_parse_support_response(&frame, &range, data) && range == ranges[r],
              "the gateway recognises the simulator's bitmap");
        (void)obd_support_apply(&support, range, data);
    }
    CHECK(obd_support_declares_next(&support, 0x00u) && obd_support_declares_next(&support, 0x20u) &&
              !obd_support_declares_next(&support, 0x40u),
          "ranges 0x20 and 0x40 are declared, nothing beyond");

    bool consistent = true;
    for (unsigned pid = 1u; pid <= 0x60u; pid++) {
        obd_sim::Frame answer;
        const bool answered = ask(state, obd_sim::PHYSICAL_REQUEST_ID, (uint8_t)pid, &answer);
        if (answered != obd_support_has(&support, (uint8_t)pid)) {
            consistent = false;
            printf("  PID 0x%02X: answered=%d declared=%d\n", pid, answered, obd_support_has(&support, (uint8_t)pid));
        }
    }
    CHECK(consistent, "a PID is answered exactly when the bitmaps declare it");
}

static VehicleModel at(uint32_t ms)
{
    VehicleModel model;
    model.advance(ms);
    return model;
}

static void test_engine_speed_follows_the_pedal()
{
    VehicleModel model = at(9900u);
    const float idle = model.state().engine_rpm;
    CHECK(fabsf(idle - VehicleModel::IDLE_RPM) < 30.0f, "the engine idles, with a little wander, before the drive starts");

    model.advance(200u);  /* the first leg of the loop, 10 s of idle, ends and the driver pulls away */
    const float soon = model.state().engine_rpm;
    CHECK(model.state().pedal > 0.0f, "the pedal is down after 10 s");
    CHECK(soon > idle && soon - idle < 300.0f, "engine speed starts rising without jumping");

    /* Within first gear the engine speed climbs; the wander never makes it fall noticeably. */
    bool climbing = true;
    float previous = soon;
    for (int i = 0; i < 40; i++) {
        model.advance(100u);
        climbing = climbing && model.state().engine_rpm > previous - 20.0f && model.state().gear == 1u;
        previous = model.state().engine_rpm;
    }
    CHECK(climbing, "in first gear engine speed climbs over the following four seconds");
    CHECK(model.state().engine_rpm > soon + 200.0f, "and ends well above where it started");
    CHECK(model.state().speed_kmh > 10.0f, "the vehicle drives off");
}

static void test_the_model_does_not_depend_on_how_often_it_is_called()
{
    /* The Uno calls advance() every pass of loop(), a few milliseconds apart and unevenly;
     * the host tests call it in whole ticks. Both must see the same car. */
    VehicleModel coarse;
    VehicleModel fine;
    const uint32_t uneven[] = {1u, 3u, 7u, 2u, 13u, 4u, 41u, 5u, 1u, 23u};
    uint32_t elapsed = 0u;
    for (unsigned i = 0u; elapsed < 60000u; i++) {
        const uint32_t dt = uneven[i % (sizeof(uneven) / sizeof(uneven[0]))];
        fine.advance(dt);
        elapsed += dt;
    }
    coarse.advance(elapsed);
    CHECK(fine.elapsed_ms() == coarse.elapsed_ms() && fine.elapsed_ms() == elapsed - elapsed % VehicleModel::TICK_MS,
          "time passes in whole ticks either way");
    const VehicleState &a = fine.state();
    const VehicleState &b = coarse.state();
    CHECK(a.pedal == b.pedal && a.gear == b.gear && a.engine_rpm == b.engine_rpm && a.speed_kmh == b.speed_kmh &&
              a.coolant_c == b.coolant_c && a.oil_c == b.oil_c && a.intake_c == b.intake_c &&
              a.intake_kpa == b.intake_kpa && a.engine_load_pct == b.engine_load_pct &&
              a.throttle_pct == b.throttle_pct && a.maf_gs == b.maf_gs && a.fuel_rate_lh == b.fuel_rate_lh &&
              a.fuel_level_pct == b.fuel_level_pct && a.module_voltage_v == b.module_voltage_v,
          "uneven millisecond steps and one long step give exactly the same values");
}

static void test_the_tank_empties()
{
    VehicleModel model;
    const float full = model.state().fuel_level_pct;
    model.advance(VehicleModel::CYCLE_MS);
    const float after_one_loop = model.state().fuel_level_pct;
    model.advance(9u * VehicleModel::CYCLE_MS);
    CHECK(after_one_loop < full && full - after_one_loop < 0.5f, "one loop uses a little fuel");
    CHECK(model.state().fuel_level_pct < after_one_loop - 0.5f && model.state().fuel_level_pct > full - 5.0f,
          "half an hour of town driving uses a litre or so of a 40 L tank");
}

static void test_the_engine_warms_up()
{
    VehicleModel model;
    const float cold = model.state().coolant_c;
    float hottest = cold;
    float at_five_minutes = cold;
    for (uint32_t s = 1u; s <= 1200u; s++) {
        model.advance(1000u);
        hottest = model.state().coolant_c > hottest ? model.state().coolant_c : hottest;
        if (s == 300u) {
            at_five_minutes = model.state().coolant_c;
        }
    }
    CHECK(cold < 45.0f, "the engine starts warm-ish, as just started");
    CHECK(at_five_minutes > 80.0f, "after five minutes the coolant is above 80 °C");
    CHECK(hottest <= VehicleModel::WARM_COOLANT_C + 0.5f, "the coolant never overshoots 92 °C noticeably");
    CHECK(model.state().oil_c > cold && model.state().oil_c < model.state().coolant_c + 6.0f,
          "the oil warms behind the coolant");
}

static void test_the_city_loop_looks_like_town_driving()
{
    VehicleModel model;
    float max_speed = 0.0f, max_rpm = 0.0f, min_intake = 255.0f, max_intake = 0.0f, max_maf = 0.0f;
    float min_throttle = 100.0f, max_throttle = 0.0f, min_volt = 99.0f;
    int stops = 0, stopped_run = 0, overrun_s = 0, gears_seen = 0;
    for (uint32_t t = 0u; t < VehicleModel::CYCLE_MS; t += 1000u) {
        model.advance(1000u);
        const VehicleState &v = model.state();
        max_speed = v.speed_kmh > max_speed ? v.speed_kmh : max_speed;
        max_rpm = v.engine_rpm > max_rpm ? v.engine_rpm : max_rpm;
        min_intake = v.intake_kpa < min_intake ? v.intake_kpa : min_intake;
        max_intake = v.intake_kpa > max_intake ? v.intake_kpa : max_intake;
        max_maf = v.maf_gs > max_maf ? v.maf_gs : max_maf;
        min_throttle = v.throttle_pct < min_throttle ? v.throttle_pct : min_throttle;
        max_throttle = v.throttle_pct > max_throttle ? v.throttle_pct : max_throttle;
        min_volt = v.module_voltage_v < min_volt ? v.module_voltage_v : min_volt;
        gears_seen |= 1 << v.gear;
        overrun_s += (v.gear > 0u && v.pedal == 0.0f && v.speed_kmh > 8.0f && v.intake_kpa < 30.0f) ? 1 : 0;
        stopped_run = v.speed_kmh < 0.5f ? stopped_run + 1 : 0;
        stops += stopped_run == 8 ? 1 : 0;
    }
    CHECK(stops == 3, "the loop idles at the start and stops at two lights");
    CHECK(max_speed > 55.0f && max_speed < 65.0f, "the top speed is about 60 km/h");
    CHECK(max_rpm > 1800.0f && max_rpm < 4000.0f, "engine speed stays in a town range");
    CHECK(min_intake < 30.0f && max_intake > 90.0f, "the manifold goes from overrun vacuum to near atmospheric");
    CHECK(overrun_s >= 3, "lifting off in gear pulls the manifold down for a few seconds");
    CHECK(min_throttle < 30.0f && max_throttle > 55.0f, "the throttle sensor reads 27 % closed and opens with the pedal");
    CHECK(max_maf > 8.0f && max_maf < 40.0f, "air flow reaches a few tens of g/s");
    CHECK(min_volt > 13.0f, "the alternator holds the voltage");
    CHECK((gears_seen & 0x1E) == 0x1E, "gears one to four are used");
}

/** Half the step of each PID's encoding, the most a round trip may lose. */
static double resolution(uint8_t pid)
{
    switch (pid) {
    case 0x04u: case 0x11u: case 0x2Fu: return 100.0 / 255.0;
    case 0x0Cu: return 0.25;
    case 0x10u: return 0.01;
    case 0x42u: return 0.001;
    case 0x5Eu: return 0.05;
    default: return 1.0;
    }
}

static void test_round_trip_through_the_gateway_decoder()
{
    VehicleModel model;
    const obd_pid_info_t *table = obd_pid_table();
    int compared = 0;
    bool within = true;
    for (uint32_t t = 0u; t < VehicleModel::CYCLE_MS; t += 100u) {
        model.advance(100u);
        for (unsigned i = 0u; i < OBD_PID_COUNT; i++) {
            const uint8_t pid = table[i].pid;
            obd_sim::Frame answer;
            float expected = 0.0f;
            if (!ask(model.state(), obd_sim::PHYSICAL_REQUEST_ID, pid, &answer) ||
                !obd_sim::model_value(model.state(), pid, &expected)) {
                within = false;
                continue;
            }
            const hal_can_frame_t frame = as_can(answer);
            const obd_decode_outcome_t decoded = obd_decode_frame(&frame);
            const double error = decoded.decoded ? fabs(decoded.reading.value - (double)expected) : 1e9;
            if (error > resolution(pid) / 2.0 + 1e-3) {
                if (within) {
                    printf("  t=%u ms PID 0x%02X model %.4f decoded %.4f\n", (unsigned)t, pid, (double)expected,
                           decoded.reading.value);
                }
                within = false;
            }
            compared++;
        }
    }
    CHECK(compared == (int)(OBD_PID_COUNT * (VehicleModel::CYCLE_MS / 100u)), "every PID is answered at every step");
    CHECK(within, "every decoded value equals the model value within half its PID's resolution");
}


/* ---------------------------------------------------------------- recorded drive replay */

static double replayed(uint32_t elapsed_ms, uint8_t pid)
{
    obd_sim::Frame answer;
    if (!ask_replay(elapsed_ms, pid, &answer)) {
        return -1e9;
    }
    const hal_can_frame_t frame = as_can(answer);
    const obd_decode_outcome_t decoded = obd_decode_frame(&frame);
    return decoded.decoded ? decoded.reading.value : -1e9;
}

static void test_replay_declares_and_answers_only_the_recorded_pids()
{
    obd_support_t support;
    obd_support_init(&support);
    const uint8_t ranges[3] = {0x00u, 0x20u, 0x40u};
    for (int r = 0; r < 3; r++) {
        obd_sim::Frame answer;
        CHECK(ask_replay(0u, ranges[r], &answer), "every declared range is answered");
        const hal_can_frame_t frame = as_can(answer);
        uint8_t range = 0xFFu;
        uint8_t data[4];
        CHECK(obd_parse_support_response(&frame, &range, data) && range == ranges[r],
              "the gateway recognises the replay's bitmap");
        (void)obd_support_apply(&support, range, data);
    }
    CHECK(!obd_support_declares_next(&support, 0x40u), "nothing is declared beyond range 0x40");

    int measured = 0;
    bool consistent = true;
    for (unsigned pid = 1u; pid <= 0x60u; pid++) {
        obd_sim::Frame answer;
        const bool answered = ask_replay(0u, (uint8_t)pid, &answer);
        if (answered != obd_support_has(&support, (uint8_t)pid)) {
            consistent = false;
        }
        if (answered && !obd_is_support_pid((uint8_t)pid)) {
            measured++;
        }
    }
    CHECK(consistent, "a PID is answered exactly when the bitmaps declare it");
    CHECK(measured == DRIVE_PID_COUNT_EXPECTED, "exactly the ten PIDs the car answered");
    obd_sim::Frame answer;
    CHECK(!ask_replay(0u, 0x5Eu, &answer) && !ask_replay(0u, 0x2Fu, &answer),
          "fuel rate and fuel level, which the car did not answer, stay silent");
}

static void test_replay_gives_the_recorded_values()
{
    /* experiments/results/20261007-vehicle-drive/readings.csv, seconds 0 and 1 */
    CHECK(fabs(replayed(0u, 0x0Cu) - 650.25) < 1e-9, "engine speed at 0 s");
    CHECK(fabs(replayed(999u, 0x10u) - 9.73) < 1e-9, "air flow during the whole of second 0");
    CHECK(fabs(replayed(1000u, 0x0Cu) - 648.5) < 1e-9, "engine speed at 1 s");
    CHECK(fabs(replayed(1500u, 0x10u) - 7.94) < 1e-9, "air flow at 1 s");
    CHECK(fabs(replayed(0u, 0x05u) - 78.0) < 1e-9, "coolant temperature");
    CHECK(fabs(replayed(0u, 0x42u) - 14.0) < 1e-9, "module voltage");
    CHECK(fabs(replayed(0u, 0x04u) - 20.0 * 100.0 / 255.0) < 1e-9, "engine load, the byte the car sent");
}

static void test_replay_loops()
{
    const uint32_t length_ms = (uint32_t)obd_sim::DRIVE_SECONDS * 1000u;
    CHECK(obd_sim::ReplaySource(length_ms - 1u).second() == obd_sim::DRIVE_SECONDS - 1u, "last second");
    CHECK(obd_sim::ReplaySource(length_ms).second() == 0u, "wraps to the start");
    CHECK(fabs(replayed(length_ms + 1000u, 0x0Cu) - 648.5) < 1e-9, "the second lap repeats the first");
}

/** Every byte the replay sends equals the profile CSV the virtual device reads. */
static void test_replay_matches_the_profile_csv()
{
    FILE *csv = fopen(PROFILE_CSV, "r");
    CHECK(csv != NULL, "profile CSV found");
    if (csv == NULL) {
        return;
    }
    char line[256];
    CHECK(fgets(line, sizeof line, csv) != NULL, "header");
    int rows = 0;
    bool equal = true;
    while (fgets(line, sizeof line, csv) != NULL) {
        char *cursor = line;
        const long second = strtol(cursor, &cursor, 10);
        for (uint8_t i = 0u; i < obd_sim::DRIVE_PID_COUNT && equal; i++) {
            cursor++; /* the comma */
            uint8_t sent[4];
            const uint8_t width = obd_sim::ReplaySource((uint32_t)second * 1000u).encode(obd_sim::DRIVE_PIDS[i], sent);
            for (uint8_t b = 0u; b < width; b++) {
                unsigned value = 0u;
                if (sscanf(cursor + 2 * b, "%2x", &value) != 1 || value != sent[b]) {
                    equal = false;
                    printf("  second %ld PID 0x%02X differs\n", second, obd_sim::DRIVE_PIDS[i]);
                }
            }
            cursor += 2 * width;
        }
        rows++;
    }
    fclose(csv);
    CHECK(rows == (int)obd_sim::DRIVE_SECONDS, "one CSV row per replayed second");
    CHECK(equal, "the simulator and the virtual device replay the same bytes");
}

int main()
{
    printf("simulated ECU host tests\n");

    test_speed_is_encoded_as_in_the_standard();
    test_functional_and_physical_requests_are_answered();
    test_unsupported_pid_is_silent();
    test_bitmaps_match_what_is_answered();
    test_engine_speed_follows_the_pedal();
    test_the_model_does_not_depend_on_how_often_it_is_called();
    test_the_tank_empties();
    test_the_engine_warms_up();
    test_the_city_loop_looks_like_town_driving();
    test_round_trip_through_the_gateway_decoder();
    test_replay_declares_and_answers_only_the_recorded_pids();
    test_replay_gives_the_recorded_values();
    test_replay_loops();
    test_replay_matches_the_profile_csv();

    printf("%d checks, %d failures\n", s_checks, s_failures);
    printf(s_failures == 0 ? "PASS\n" : "FAIL\n");
    return s_failures == 0 ? 0 : 1;
}
