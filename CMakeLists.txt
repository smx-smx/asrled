cmake_minimum_required(VERSION 3.23)
project(asrled C CXX ASM)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_SHARED_LIBRARY_PREFIX "")

include(FetchContent)
set(FETCHCONTENT_QUIET OFF)

FetchContent_Declare(
    minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
    GIT_TAG master
)
FetchContent_MakeAvailable(minhook)

add_custom_command(
    OUTPUT
        ${CMAKE_BINARY_DIR}/version.def
        ${CMAKE_BINARY_DIR}/version.S
    COMMAND php ${CMAKE_SOURCE_DIR}/makedef.php
        C:\\Windows\\SysWow64\\version.dll
        1>${CMAKE_BINARY_DIR}/version.def
        2>${CMAKE_BINARY_DIR}/version.S
)

add_custom_command(
    OUTPUT ${CMAKE_BINARY_DIR}/wICPFLASH.exe
    COMMAND ${CMAKE_COMMAND} -E copy
        ${CMAKE_SOURCE_DIR}/wICPFLASH.exe
        ${CMAKE_BINARY_DIR}/wICPFLASH.exe
)

add_custom_command(
    OUTPUT
        ${CMAKE_BINARY_DIR}/libezdotnet_shared.dll
        ${CMAKE_BINARY_DIR}/libcoreclrhost.dll
    COMMAND
        ${CMAKE_COMMAND} -E copy
            ${CMAKE_SOURCE_DIR}/ezdotnet_bin/libezdotnet_shared.dll
            ${CMAKE_BINARY_DIR}/libezdotnet_shared.dll
    COMMAND
        ${CMAKE_COMMAND} -E copy
            ${CMAKE_SOURCE_DIR}/ezdotnet_bin/libcoreclrhost.dll
            ${CMAKE_BINARY_DIR}/libcoreclrhost.dll
)

add_library(version SHARED
    proxy.cpp
    ${CMAKE_BINARY_DIR}/version.S
    ${CMAKE_BINARY_DIR}/version.def
    # binary deps start
    ${CMAKE_BINARY_DIR}/wICPFLASH.exe
    ${CMAKE_BINARY_DIR}/libezdotnet_shared.dll
    ${CMAKE_BINARY_DIR}/libcoreclrhost.dll
    # binary deps end
    main.cpp util.h)
target_compile_options(version PRIVATE -fvisibility=hidden)
target_link_options(version PRIVATE
    LINKER:--enable-stdcall-fixup)

#find_library(MinHook_LIBRARY MinHook REQUIRED)
#target_link_libraries(version ${MinHook_LIBRARY})
target_link_libraries(version minhook)


#add_executable(asrled proxy.cpp)
