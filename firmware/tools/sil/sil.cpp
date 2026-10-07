/**
 * @file sil.cpp
 * @brief Software-in-the-loop bench: the gateway's scheduler and decoder against the
 *        simulator's vehicle model and Mode 01 responder, in simulated time.
 *
 * The same code that runs on the ESP32 (components/obd) and on the Uno (vehicle_model,
 * obd_responder) exchanges frames here without a bus. Output is the gateway's console
 * format, so experiments/runner/drive_charts.py draws it like a recorded drive.
 *
 *   make && ./build/sil 300 > sil.log     # five simulated minutes
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "obd.h"
#include "obd_responder.h"
#include "obd_sched.h"
#include "vehicle_model.h"

namespace {

const uint64_t STEP_US = 1000u;           /* the gateway's main loop runs every millisecond */
const uint64_t ECU_LATENCY_US = 3000u;    /* the real engine ECU answered in about 3 ms */

struct Pending {
    bool active;
    uint64_t at_us;
    hal_can_frame_t frame;
};

hal_can_frame_t to_can(const obd_sim::Frame &f, uint64_t at_us)
{
    hal_can_frame_t out;
    memset(&out, 0, sizeof(out));
    out.id = f.id;
    out.dlc = f.dlc;
    memcpy(out.data, f.data, f.dlc);
    out.timestamp_us = at_us;
    return out;
}

}  // namespace

int main(int argc, char **argv)
{
    const double seconds = argc > 1 ? atof(argv[1]) : 120.0;
    const uint64_t end_us = (uint64_t)(seconds * 1e6);

    VehicleModel model;
    obd_sched_t sched;
    obd_sched_config_t config = obd_sched_default_config();
    obd_sched_init(&sched, &config);

    Pending answer = {false, 0u, {}};
    uint64_t model_ms = 0u;
    printf("I (0) gateway: software-in-the-loop bench, %.0f s simulated, ECU latency %u us\n", seconds,
           (unsigned)ECU_LATENCY_US);

    for (uint64_t now = 0u; now <= end_us; now += STEP_US) {
        /* The vehicle model runs on the simulator's clock, in whole milliseconds. */
        const uint64_t now_ms = now / 1000u;
        model.advance((uint32_t)(now_ms - model_ms));
        model_ms = now_ms;

        obd_sched_outcome_t out;
        if (answer.active && answer.at_us <= now) {
            answer.active = false;
            obd_sched_on_frame(&sched, &answer.frame, now, &out);
            if (out.event == OBD_EVENT_READING) {
                printf("OBD,%llu,0x%02X,%s,%.2f,%s,0x%03X\n", (unsigned long long)(answer.frame.timestamp_us / 1000u),
                       out.reading.pid, out.reading.name, out.reading.value, out.reading.unit, (unsigned)out.source_id);
            } else if (out.event == OBD_EVENT_DISCOVERED) {
                printf("I (%llu) gateway: discovered %u supported PIDs\n", (unsigned long long)now_ms,
                       (unsigned)sched.poll_count);
            }
        } else {
            obd_sched_on_tick(&sched, now, &out);
        }

        /* The ECU sees the request on the bus and answers after its latency. */
        if (out.send_frame) {
            obd_sim::Frame reply;
            if (obd_sim::respond(model.state(), out.frame.id, out.frame.data, out.frame.dlc, &reply)) {
                answer = {true, now + ECU_LATENCY_US, to_can(reply, now + ECU_LATENCY_US)};
            }
        }
    }

    const obd_sched_counters_t &c = sched.counters;
    printf("I (%llu) gateway: stats | cycles=%u requests=%u answers=%u timeouts=%u negative=%u\n",
           (unsigned long long)(end_us / 1000u), (unsigned)c.cycles, (unsigned)c.requests, (unsigned)c.answers,
           (unsigned)c.timeouts, (unsigned)c.negative);
    return 0;
}
