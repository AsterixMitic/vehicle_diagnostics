# Host tests for the simulated ECU: the vehicle model and the Mode 01 responder, checked
# against the gateway's own decoder, so the round trip the bench performs is tested here.
#
# Needs nothing but a C and a C++ compiler. Arduino ignores this directory.
#
#   make            build and run
#   make clean

CC       ?= cc
CXX      ?= c++
WARN     := -Wall -Wextra -Werror -Wconversion -Wshadow
CFLAGS   ?= -std=c11 -O1 -g $(WARN)
CXXFLAGS ?= -std=c++11 -O1 -g $(WARN)

SIM      := ../..
GATEWAY  := ../../../gateway/components
INCLUDES := -I$(SIM) -I$(GATEWAY)/gw_hal/include -I$(GATEWAY)/obd/include
BUILD    := build

OBJECTS := $(BUILD)/vehicle_model.o $(BUILD)/obd_responder.o $(BUILD)/obd_decode.o $(BUILD)/obd_support.o
TARGET  := $(BUILD)/sim_tests

.PHONY: all run clean
all: run

$(BUILD):
	mkdir -p $(BUILD)

$(BUILD)/%.o: $(SIM)/%.cpp $(SIM)/%.h | $(BUILD)
	$(CXX) $(CXXFLAGS) $(INCLUDES) -c $< -o $@

$(BUILD)/obd_%.o: $(GATEWAY)/obd/obd_%.c | $(BUILD)
	$(CC) $(CFLAGS) $(INCLUDES) -c $< -o $@

$(TARGET): $(OBJECTS) test_sim.cpp $(SIM)/vehicle_model.h $(SIM)/obd_responder.h
	$(CXX) $(CXXFLAGS) $(INCLUDES) test_sim.cpp $(OBJECTS) -o $@ -lm

run: $(TARGET)
	@./$(TARGET)

clean:
	rm -rf $(BUILD)
