cmake_minimum_required(VERSION 3.24)
project(MicStudioEcho LANGUAGES C CXX)
include(FetchContent)
include(CTest)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_C_STANDARD 99)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
FetchContent_Declare(speexdsp
  URL https://downloads.xiph.org/releases/speex/speexdsp-1.2.1.tar.gz
  URL_HASH SHA256=8c777343e4a6399569c72abc38a95b24db56882c83dbdb6c6424a5f4aeb54d3d
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(speexdsp)
add_library(speex_echo STATIC
  ${speexdsp_SOURCE_DIR}/libspeexdsp/mdf.c
  ${speexdsp_SOURCE_DIR}/libspeexdsp/fftwrap.c
  ${speexdsp_SOURCE_DIR}/libspeexdsp/smallft.c)
target_include_directories(speex_echo PUBLIC ${speexdsp_SOURCE_DIR}/include PRIVATE
  ${speexdsp_SOURCE_DIR}/win32 ${speexdsp_SOURCE_DIR}/libspeexdsp)
target_compile_definitions(speex_echo PRIVATE HAVE_CONFIG_H _USE_MATH_DEFINES)
if(MSVC)
  target_compile_options(speex_echo PRIVATE /W2 /wd4244 /wd4305)
endif()
add_library(MicStudioEcho SHARED MicStudioEcho.cpp)
target_compile_definitions(MicStudioEcho PRIVATE MICSTUDIO_ECHO_BUILD)
target_link_libraries(MicStudioEcho PRIVATE speex_echo)
target_include_directories(MicStudioEcho PRIVATE ${speexdsp_SOURCE_DIR}/include)
if(MSVC)
  target_compile_options(MicStudioEcho PRIVATE /W4 /WX)
endif()
configure_file(${speexdsp_SOURCE_DIR}/COPYING ${CMAKE_CURRENT_BINARY_DIR}/SpeexDSP-COPYING.txt COPYONLY)
add_executable(MicStudioEchoTests ../../tests/MicStudio.Echo.Native.Tests/Tests.cpp)
target_link_libraries(MicStudioEchoTests PRIVATE MicStudioEcho)
target_include_directories(MicStudioEchoTests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
add_test(NAME NativeEchoSignals COMMAND MicStudioEchoTests)
