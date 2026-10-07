#pragma once

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Run every scheduler and error-guard test.
 * @return the number of failed checks; zero means all passed.
 */
int obd_sched_tests_run(void);

#ifdef __cplusplus
}
#endif
