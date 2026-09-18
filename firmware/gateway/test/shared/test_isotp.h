#pragma once

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Run every ISO-TP transport test.
 * @return the number of failed checks; zero means all passed.
 */
int isotp_tests_run(void);

#ifdef __cplusplus
}
#endif
