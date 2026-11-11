cmake_minimum_required(VERSION 3.24)
if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    project(HS2ManagerNativeCore LANGUAGES C CXX)
    enable_testing()
endif()
include("${CMAKE_CURRENT_LIST_DIR}/../HS2Manager.NativeScene/Archive/Archive.cmake")

add_library(
    hs2_core STATIC
    Common/Core.cpp
    Serialization/MessagePack.cpp
    Serialization/CardData.cpp
    Library/Repository.cpp
    Library/FileService.cpp
    Editing/EditService.cpp
    Editing/EditRules.cpp
    Editing/EditSchema.cpp
    Mods/ModIndex.cpp
    Mods/PluginIndex.cpp
    Preview/PreviewResourceContext.cpp
    Preview/TemporaryPreviewSession.cpp
)
target_compile_features(hs2_core PUBLIC cxx_std_20)
target_include_directories(hs2_core PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_definitions(hs2_core PUBLIC UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(hs2_core PRIVATE /utf-8 /W4 /permissive- /EHsc)
target_link_libraries(hs2_core PUBLIC winsqlite3 bcrypt ole32 xmllite shlwapi hs2_archive)
if(NOT TARGET hs2_scene)
    add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../HS2Manager.NativeScene" "${CMAKE_CURRENT_BINARY_DIR}/scene")
endif()
add_library(hs2_card_export STATIC Export/CardExport.cpp Export/CardExportDependencies.cpp)
target_compile_features(hs2_card_export PUBLIC cxx_std_20)
target_include_directories(hs2_card_export PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_options(hs2_card_export PRIVATE /utf-8 /W4 /permissive- /EHsc)
target_link_libraries(hs2_card_export PUBLIC hs2_core hs2_scene)
add_library(hs2_core_components STATIC Editing/CharacterEditComponents.cpp)
target_compile_features(hs2_core_components PUBLIC cxx_std_20)
target_include_directories(hs2_core_components PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_options(hs2_core_components PRIVATE /utf-8 /W4 /permissive- /EHsc)
target_link_libraries(hs2_core_components PUBLIC hs2_core hs2_scene)
option(HS2_CORE_BUILD_TESTS "Build native core behavioral checks" ON)
if(HS2_CORE_BUILD_TESTS)
    add_subdirectory(
        "${CMAKE_CURRENT_SOURCE_DIR}/../../tests/HS2Manager.CppCoreChecks" "${CMAKE_CURRENT_BINARY_DIR}/checks"
    )
endif()
