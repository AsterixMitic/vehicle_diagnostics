# Software-in-the-loop bench: gateway scheduler and decoder against the simulator's model.
#   make            build
#   ./build/sil 300 > sil.log
CC       ?= cc
CXX      ?= c++
WARN     := -Wall -Wextra -Werror -Wshadow
CFLAGS   ?= -std=c11 -O2 $(WARN)
CXXFLAGS ?= -std=c++11 -O2 $(WARN)
GW       := ../../gateway/components
SIM      := ../../sim-ecu
INCLUDES := -I$(GW)/gw_hal/include -I$(GW)/obd/include -I$(GW)/envelope/include -I$(SIM)
BUILD    := build
C_SRC    := $(GW)/obd/obd_decode.c $(GW)/obd/obd_support.c $(GW)/obd/obd_sched.c $(GW)/envelope/envelope.c
CXX_SRC  := $(SIM)/vehicle_model.cpp $(SIM)/obd_responder.cpp sil.cpp

all: $(BUILD)/sil

$(BUILD)/sil: $(C_SRC) $(CXX_SRC) | $(BUILD)
	$(CC) $(CFLAGS) $(INCLUDES) -c $(GW)/obd/obd_decode.c -o $(BUILD)/obd_decode.o
	$(CC) $(CFLAGS) $(INCLUDES) -c $(GW)/obd/obd_support.c -o $(BUILD)/obd_support.o
	$(CC) $(CFLAGS) $(INCLUDES) -c $(GW)/obd/obd_sched.c -o $(BUILD)/obd_sched.o
	$(CC) $(CFLAGS) $(INCLUDES) -c $(GW)/envelope/envelope.c -o $(BUILD)/envelope.o
	$(CXX) $(CXXFLAGS) $(INCLUDES) $(CXX_SRC) $(BUILD)/obd_decode.o $(BUILD)/obd_support.o $(BUILD)/obd_sched.o $(BUILD)/envelope.o -o $@

$(BUILD):
	mkdir -p $(BUILD)

clean:
	rm -rf $(BUILD)
