#pragma once

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Run every envelope serialization test.
 * @return the number of failed checks; zero means all passed.
 */
int envelope_tests_run(void);

#ifdef __cplusplus
}
#endif
