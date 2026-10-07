/**
 * @file fixture_target.c
 * @brief Target harness for the OBD fixture. The emulator has no file system, so the build
 *        embeds the backend's capture files (EMBED_TXTFILES adds a terminating NUL).
 */
#include "test_obd.h"

extern const char synthetic_obd_log_start[] asm("_binary_synthetic_obd_mode01_log_start");
extern const char synthetic_obd_expected_start[] asm("_binary_synthetic_obd_mode01_expected_json_start");

const char *obd_fixture_log(void)
{
    return synthetic_obd_log_start;
}

const char *obd_fixture_expected(void)
{
    return synthetic_obd_expected_start;
}
