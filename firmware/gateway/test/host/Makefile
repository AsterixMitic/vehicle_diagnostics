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

INCLUDES := \
	-I$(COMPONENTS)/gw_hal/include \
	-I$(COMPONENTS)/can_link/include \
	-I$(COMPONENTS)/isotp/include \
	-I$(SHARED)

SOURCES := \
	$(COMPONENTS)/can_link/can_ring_buffer.c \
	$(COMPONENTS)/isotp/isotp.c \
	$(SHARED)/test_can_ring_buffer.c \
	$(SHARED)/test_isotp.c \
	main.c

TARGET := $(BUILD)/host_tests

.PHONY: all run clean
all: run

$(TARGET): $(SOURCES) | $(BUILD)
	$(CC) $(CFLAGS) $(INCLUDES) $(SOURCES) -o $@ $(LDFLAGS)

$(BUILD):
	mkdir -p $(BUILD)

run: $(TARGET)
	@./$(TARGET)

clean:
	rm -rf $(BUILD)
