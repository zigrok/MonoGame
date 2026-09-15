cmake_minimum_required(VERSION 3.18)
project(MonoGameBrowser LANGUAGES C CXX)
if(NOT EMSCRIPTEN)
  message(FATAL_ERROR "Use the pinned .NET Emscripten toolchain via native/browser/build.sh")
endif()
set(SDL_SHARED OFF CACHE BOOL "" FORCE)
set(SDL_STATIC ON CACHE BOOL "" FORCE)
set(SDL_TESTS OFF CACHE BOOL "" FORCE)
set(SDL_EXAMPLES OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(BUILD_SDL3 ON CACHE BOOL "" FORCE)
set(XNASONG OFF CACHE BOOL "" FORCE)
add_subdirectory(../monogame/external/sdl3 sdl3)
add_subdirectory(../monogame/external/faudio faudio)
add_library(mgruntime STATIC
  ../monogame/sdl/MGP_sdl.cpp
  ../monogame/common/MG_Asset.cpp
  ../monogame/common/MGI.cpp
  ../monogame/common/MGM.cpp
  ../monogame/common/mg_audio.cpp
  ../monogame/common/mg_hash.cpp
  ../monogame/common/mg_library.cpp
  ../monogame/faudio/MGA_faudio.cpp
  MGG_gles.cpp MGM_wav.cpp)
set_target_properties(mgruntime PROPERTIES PREFIX "")
target_compile_features(mgruntime PRIVATE cxx_std_17)
target_compile_definitions(mgruntime PRIVATE MG_SDL3 MG_GLES SDL_ENABLE_OLD_NAMES DLL_EXPORT)
target_include_directories(mgruntime PRIVATE
  ../monogame/include ../monogame/common ../../external/stb
  ../../Artifacts/browser/shaders)
target_link_libraries(mgruntime PRIVATE SDL3::SDL3-static FAudio-static)
