/**
 * @file main.c
 * @brief Host entry point for the protocol-layer unit tests.
 *
 * Runs the same assertions the emulated target runs, on a development machine, with no
 * ESP-IDF and no hardware.
 */
#include <stdio.h>

#include "test_can_ring_buffer.h"

int main(void)
{
    printf("host unit tests\n");
    const int failures = can_ring_buffer_tests_run();

    if (failures == 0) {
        printf("PASS\n");
        return 0;
    }

    printf("FAIL: %d check(s) failed\n", failures);
    return 1;
}
