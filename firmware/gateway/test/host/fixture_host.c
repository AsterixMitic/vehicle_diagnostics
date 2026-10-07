/**
 * @file fixture_host.c
 * @brief Host harness for the OBD fixture: reads the backend's capture files from the
 *        repository, so the gateway and the backend decoders are checked against one copy.
 */
#include <stdio.h>

#include "test_obd.h"

#ifndef OBD_FIXTURE_DIR
#error "OBD_FIXTURE_DIR must name experiments/captures"
#endif

#define FIXTURE_MAX_BYTES 16384

static char s_log[FIXTURE_MAX_BYTES];
static char s_expected[FIXTURE_MAX_BYTES];

static const char *load(const char *name, char *buffer)
{
    char path[512];
    (void)snprintf(path, sizeof(path), "%s/%s", OBD_FIXTURE_DIR, name);
    FILE *file = fopen(path, "rb");
    if (file == NULL) {
        printf("  cannot open %s\n", path);
        return NULL;
    }
    const size_t read = fread(buffer, 1, FIXTURE_MAX_BYTES - 1, file);
    (void)fclose(file);
    buffer[read] = '\0';
    return buffer;
}

const char *obd_fixture_log(void)
{
    return load("synthetic-obd-mode01.log", s_log);
}

const char *obd_fixture_expected(void)
{
    return load("synthetic-obd-mode01.expected.json", s_expected);
}
