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
 *
 * With --envelopes P0|P2 it prints, instead of the console, one line per poll cycle: the exact
 * envelope the gateway would publish (components/envelope), paced in real time with
 * --realtime, so the serialization is checked end to end against the running backend:
 *
 *   ./build/sil 60 --envelopes P2 --realtime | \
 *       docker exec -i <mosquitto> mosquitto_pub -t veh/sil-car/dev/sil-01/tel/signals -q 1 -l
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "envelope.h"

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

int64_t wall_now_us()
{
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)ts.tv_sec * 1000000 + ts.tv_nsec / 1000;
}

struct Envelopes {
    bool on;
    bool realtime;
    envelope_mode_t mode;
    const char *device;
    const char *vehicle;
    uint64_t sequence;
    int64_t wall_start_us;
    obd_reading_t readings[OBD_PID_COUNT];
    hal_can_frame_t frames[OBD_PID_COUNT];
    size_t count;
    uint64_t first_us;  /* simulated receive time of the cycle's first answer */

    void collect(const obd_reading_t &r, const hal_can_frame_t &f)
    {
        if (count == 0u) {
            first_us = f.timestamp_us;
        }
        if (count < OBD_PID_COUNT) {
            readings[count] = r;
            frames[count] = f;
            count++;
        }
    }

    void flush()
    {
        if (count == 0u) {
            return;
        }
        const int64_t at = wall_start_us + (int64_t)first_us;
        if (realtime) {
            const int64_t wait = at - wall_now_us();
            if (wait > 0) {
                struct timespec ts = {(time_t)(wait / 1000000), (long)(wait % 1000000) * 1000L};
                nanosleep(&ts, NULL);
            }
        }
        uint8_t trace[16];
        uint8_t span[8];
        for (uint8_t &b : trace) b = (uint8_t)(rand() & 0xFF);
        for (uint8_t &b : span) b = (uint8_t)(rand() & 0xFF);
        char traceparent[ENVELOPE_TRACEPARENT_LEN];
        envelope_traceparent(traceparent, trace, span);
        const envelope_header_t header = {device, vehicle, sequence++, at, traceparent, mode};
        static char text[2048];
        if (envelope_write(text, sizeof(text), &header, readings, count, frames, count) > 0) {
            printf("%s\n", text);
            fflush(stdout);
        }
        count = 0u;
    }
};

int main(int argc, char **argv)
{
    const double seconds = argc > 1 ? atof(argv[1]) : 120.0;
    const uint64_t end_us = (uint64_t)(seconds * 1e6);

    Envelopes env = {false, false, ENVELOPE_MODE_P2, "sil-01", "sil-car", 1u, wall_now_us(), {}, {}, 0u, 0u};
    for (int i = 2; i < argc; i++) {
        if (strcmp(argv[i], "--envelopes") == 0 && i + 1 < argc) {
            env.on = true;
            env.mode = strcmp(argv[++i], "P0") == 0 ? ENVELOPE_MODE_P0 : ENVELOPE_MODE_P2;
        } else if (strcmp(argv[i], "--realtime") == 0) {
            env.realtime = true;
        } else if (strcmp(argv[i], "--device") == 0 && i + 1 < argc) {
            env.device = argv[++i];
        } else if (strcmp(argv[i], "--vehicle") == 0 && i + 1 < argc) {
            env.vehicle = argv[++i];
        } else if (strcmp(argv[i], "--sequence") == 0 && i + 1 < argc) {
            env.sequence = strtoull(argv[++i], NULL, 10);
        }
    }
    srand((unsigned)wall_now_us());

    VehicleModel model;
    obd_sched_t sched;
    obd_sched_config_t config = obd_sched_default_config();
    obd_sched_init(&sched, &config);

    Pending answer = {false, 0u, {}};
    uint64_t model_ms = 0u;
    if (!env.on) {
        printf("I (0) gateway: software-in-the-loop bench, %.0f s simulated, ECU latency %u us\n", seconds,
               (unsigned)ECU_LATENCY_US);
    }

    for (uint64_t now = 0u; now <= end_us; now += STEP_US) {
        /* The vehicle model runs on the simulator's clock, in whole milliseconds. */
        const uint64_t now_ms = now / 1000u;
        model.advance((uint32_t)(now_ms - model_ms));
        model_ms = now_ms;

        obd_sched_outcome_t out;
        if (answer.active && answer.at_us <= now) {
            answer.active = false;
            obd_sched_on_frame(&sched, &answer.frame, now, &out);
            if (out.event == OBD_EVENT_READING && env.on) {
                env.collect(out.reading, answer.frame);
            } else if (out.event == OBD_EVENT_READING) {
                printf("OBD,%llu,0x%02X,%s,%.2f,%s,0x%03X\n", (unsigned long long)(answer.frame.timestamp_us / 1000u),
                       out.reading.pid, out.reading.name, out.reading.value, out.reading.unit, (unsigned)out.source_id);
            } else if (out.event == OBD_EVENT_DISCOVERED && !env.on) {
                printf("I (%llu) gateway: discovered %u supported PIDs\n", (unsigned long long)now_ms,
                       (unsigned)sched.poll_count);
            }
        } else {
            obd_sched_on_tick(&sched, now, &out);
        }
        /* Same rule as the gateway: the cycle is over once the scheduler waits for the next. */
        if (env.on && sched.state == OBD_SCHED_CYCLE_WAIT) {
            env.flush();
        }

        /* The ECU sees the request on the bus and answers after its latency. */
        if (out.send_frame) {
            obd_sim::Frame reply;
            if (obd_sim::respond(model.state(), out.frame.id, out.frame.data, out.frame.dlc, &reply)) {
                answer = {true, now + ECU_LATENCY_US, to_can(reply, now + ECU_LATENCY_US)};
            }
        }
    }

    if (env.on) {
        return 0;
    }
    const obd_sched_counters_t &c = sched.counters;
    printf("I (%llu) gateway: stats | cycles=%u requests=%u answers=%u timeouts=%u negative=%u\n",
           (unsigned long long)(end_us / 1000u), (unsigned)c.cycles, (unsigned)c.requests, (unsigned)c.answers,
           (unsigned)c.timeouts, (unsigned)c.negative);
    return 0;
}
