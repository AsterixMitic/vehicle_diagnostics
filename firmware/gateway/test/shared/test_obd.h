#pragma once

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Run every OBD decoder and support-bitmap test.
 * @return the number of failed checks; zero means all passed.
 */
int obd_tests_run(void);

/**
 * The backend's decoder fixture, as NUL-terminated text, supplied by each harness: the host
 * reads experiments/captures/, the target embeds the same files because it has no file
 * system. NULL when the fixture could not be loaded, which counts as a failure.
 */
const char *obd_fixture_log(void);
const char *obd_fixture_expected(void);

#ifdef __cplusplus
}
#endif
