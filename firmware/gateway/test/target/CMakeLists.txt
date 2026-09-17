cmake_minimum_required(VERSION 3.16)

# Reuse the gateway's components so the tests exercise the shipping code, not a copy.
set(EXTRA_COMPONENT_DIRS "${CMAKE_CURRENT_LIST_DIR}/../../components")

include($ENV{IDF_PATH}/tools/cmake/project.cmake)

project(gateway_target_tests C)
