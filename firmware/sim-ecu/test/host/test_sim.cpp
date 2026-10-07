/**
 * @file test_sim.cpp
 * @brief Host tests for the simulated ECU: Mode 01 answers, support bitmaps, the vehicle
 *        model's dynamics, and the round trip through the gateway's decoder.
 */
#include <math.h>
#include <stdio.h>
#include <string.h>

#include "obd.h"
#include "obd_responder.h"
#include "vehicle_model.h"

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
    VehicleModel model = at(14900u);
    const float idle = model.state().engine_rpm;
    CHECK(fabsf(idle - VehicleModel::IDLE_RPM) < 1.0f, "the engine idles before the drive starts");

    model.advance(200u);  /* the pedal goes down at 15 s */
    const float soon = model.state().engine_rpm;
    CHECK(model.state().pedal > 0.0f, "the pedal is down after 15 s");
    CHECK(soon > idle && soon - idle < 300.0f, "engine speed starts rising without jumping");

    /* Within first gear the engine speed rises step by step towards its target. */
    bool rising = true;
    float previous = soon;
    for (int i = 0; i < 20; i++) {
        model.advance(100u);
        rising = rising && model.state().engine_rpm >= previous && model.state().gear == 1u;
        previous = model.state().engine_rpm;
    }
    CHECK(rising, "in first gear engine speed only rises over the following two seconds");
    CHECK(model.state().engine_rpm > soon + 200.0f, "and ends well above where it started");
    CHECK(model.state().speed_kmh > 10.0f, "the vehicle drives off");
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
    CHECK(cold < 25.0f, "the engine starts at ambient");
    CHECK(at_five_minutes > 80.0f, "after five minutes the coolant is above 80 °C");
    CHECK(hottest <= VehicleModel::WARM_COOLANT_C + 0.5f, "the coolant never overshoots 90 °C noticeably");
    CHECK(model.state().oil_c > cold && model.state().oil_c < model.state().coolant_c + 6.0f,
          "the oil warms behind the coolant");
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

int main()
{
    printf("simulated ECU host tests\n");

    test_speed_is_encoded_as_in_the_standard();
    test_functional_and_physical_requests_are_answered();
    test_unsupported_pid_is_silent();
    test_bitmaps_match_what_is_answered();
    test_engine_speed_follows_the_pedal();
    test_the_engine_warms_up();
    test_round_trip_through_the_gateway_decoder();

    printf("%d checks, %d failures\n", s_checks, s_failures);
    printf(s_failures == 0 ? "PASS\n" : "FAIL\n");
    return s_failures == 0 ? 0 : 1;
}
