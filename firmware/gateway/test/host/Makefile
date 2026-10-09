# Host unit tests for the hardware-independent layers.
#
# Needs nothing but a C11 compiler: no ESP-IDF, no ESP32, no test framework. If this stops
# building, a vendor include has leaked into a public header and the layer is no longer
# testable off-target.
#
#   make            build and run
#   make clean

CC      ?= cc
CFLAGS  ?= -std=c11 -O1 -g -Wall -Wextra -Werror -Wconversion -Wshadow
LDFLAGS ?=

ROOT       := ../..
COMPONENTS := $(ROOT)/components
SHARED     := ../shared
BUILD      := build

# The backend's decoder fixture; both decoders are tested against the same files.
OBD_FIXTURE_DIR ?= $(abspath $(ROOT)/../../experiments/captures)

INCLUDES := \
	-I$(COMPONENTS)/gw_hal/include \
	-I$(COMPONENTS)/can_link/include \
	-I$(COMPONENTS)/isotp/include \
	-I$(COMPONENTS)/obd/include \
	-I$(COMPONENTS)/envelope/include \
	-I$(SHARED)

SOURCES := \
	$(COMPONENTS)/can_link/can_ring_buffer.c \
	$(COMPONENTS)/isotp/isotp.c \
	$(COMPONENTS)/obd/obd_decode.c \
	$(COMPONENTS)/obd/obd_support.c \
	$(COMPONENTS)/obd/obd_sched.c \
	$(COMPONENTS)/obd/obd_guard.c \
	$(COMPONENTS)/envelope/envelope.c \
	$(SHARED)/test_can_ring_buffer.c \
	$(SHARED)/test_isotp.c \
	$(SHARED)/test_obd.c \
	$(SHARED)/test_obd_sched.c \
	$(SHARED)/test_envelope.c \
	fixture_host.c \
	main.c

TARGET := $(BUILD)/host_tests

.PHONY: all run clean
all: run

$(TARGET): $(SOURCES) | $(BUILD)
	$(CC) $(CFLAGS) $(INCLUDES) -DOBD_FIXTURE_DIR='"$(OBD_FIXTURE_DIR)"' $(SOURCES) -o $@ $(LDFLAGS) -lm

$(BUILD):
	mkdir -p $(BUILD)

run: $(TARGET)
	@./$(TARGET)

clean:
	rm -rf $(BUILD)
