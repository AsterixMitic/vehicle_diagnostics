#pragma once

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Run every ring buffer test.
 * @return the number of failed checks; zero means all passed.
 */
int can_ring_buffer_tests_run(void);

#ifdef __cplusplus
}
#endif
