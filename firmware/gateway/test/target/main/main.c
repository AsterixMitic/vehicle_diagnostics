/**
 * @file main.c
 * @brief Runs the protocol-layer unit tests on the target, real or emulated.
 *
 * The same assertions as the host build, executed on Xtensa. Under the emulator this needs no
 * hardware, so the logic is verified even when the bench is not on the desk.
 *
 * Prints a fixed final line so a harness can grade the run without parsing test output.
 */
#include <stdio.h>

#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "test_can_ring_buffer.h"
#include "test_isotp.h"
#include "test_obd.h"
#include "test_obd_sched.h"
#include "test_envelope.h"

void app_main(void)
{
    printf("\ntarget unit tests (Xtensa)\n");

    int failures = 0;
    failures += can_ring_buffer_tests_run();
    failures += isotp_tests_run();
    failures += obd_tests_run();
    failures += obd_sched_tests_run();
    failures += envelope_tests_run();

    if (failures == 0) {
        printf("RESULT: PASS\n");
    } else {
        printf("RESULT: FAIL %d\n", failures);
    }

    /* Let the console flush before the emulator is torn down. */
    vTaskDelay(pdMS_TO_TICKS(200));
    printf("TESTS COMPLETE\n");
    fflush(stdout);

    /* Halt rather than reboot, so an automated run terminates instead of looping. */
    while (true) {
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}
