cmake_minimum_required(VERSION 3.20)
project(XStoreAPIUnlocker VERSION 3.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(XGameRuntime SHARED
    src/dllmain.cpp
    src/proxy.cpp
    src/proxy.h
    src/store_hooks.cpp
    src/store_hooks.h
    src/config.cpp
    src/config.h
    src/policy.h
    src/paths.h
    src/logger.h
    src/xstore_types.h
)

include(CTest)
if(BUILD_TESTING)
    add_executable(runtime_tests tests/native/runtime_tests.cpp src/config.cpp)
    target_include_directories(runtime_tests PRIVATE src)
    target_compile_definitions(runtime_tests PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX _CRT_SECURE_NO_WARNINGS)
    add_library(runtime_test_original SHARED tests/native/fake_original.cpp)
    add_dependencies(runtime_tests XGameRuntime runtime_test_original)
    add_test(NAME runtime_policy_and_hooks COMMAND runtime_tests)
endif()

set_target_properties(XGameRuntime PROPERTIES
    OUTPUT_NAME "XGameRuntime"
    PREFIX ""
    SUFFIX ".dll"
)

target_sources(XGameRuntime PRIVATE src/exports.def)

target_link_libraries(XGameRuntime PRIVATE
    kernel32
    user32
    advapi32
    shlwapi
)

target_compile_definitions(XGameRuntime PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _CRT_SECURE_NO_WARNINGS
    UNICODE
    _UNICODE
)
