cmake_minimum_required(VERSION 3.24)
if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    project(HS2ManagerNativeScene LANGUAGES C CXX)
    enable_testing()
endif()
include(Archive/Archive.cmake)

add_library(
    hs2_scene STATIC
    Scene/SceneTypes.cpp
    Assets/AssetReader.cpp
    Assets/AssetWriter.cpp
    Assets/AssetStore.cpp
    Assets/UnityBlockCache.cpp
    Animation/Animation.cpp
    Animation/ShapeRig.cpp
    Character/CharacterAssembly.cpp
    Runtime/NativeDynamics.cpp
    Runtime/ClothRuntime.cpp
    Runtime/CharacterRuntime.cpp
    Animation/PoseControllers.cpp
    ThirdParty/Lzma/LzmaDec.c
    ThirdParty/Lz4/lz4.c
)
target_compile_features(hs2_scene PUBLIC cxx_std_20)
target_link_libraries(hs2_scene PUBLIC bcrypt)
target_include_directories(hs2_scene PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_definitions(hs2_scene PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN)
if(MSVC)
    target_compile_options(hs2_scene PRIVATE /W4 /utf-8 /fp:precise)
endif()
if(BUILD_TESTING OR CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    add_executable(hs2_asset_writing_checks ../../tests/HS2Manager.CppSceneChecks/AssetWritingChecks.cpp)
    target_link_libraries(hs2_asset_writing_checks PRIVATE hs2_scene)
    target_compile_options(hs2_asset_writing_checks PRIVATE /utf-8 /W4 /EHsc)
    add_test(NAME asset_writing COMMAND hs2_asset_writing_checks)
    add_executable(
        hs2_scene_checks ../../tests/HS2Manager.CppSceneChecks/SceneChecks.cpp
                         ../../tests/HS2Manager.CppRenderingChecks/FrozenDefinition.cpp
    )
    target_link_libraries(hs2_scene_checks PRIVATE hs2_scene hs2_archive)
    target_compile_options(hs2_scene_checks PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/utf-8>)
    add_test(NAME hs2_scene_checks COMMAND hs2_scene_checks)
    add_executable(hs2_asset_loading_checks ../../tests/HS2Manager.CppSceneChecks/AssetLoadingChecks.cpp)
    target_link_libraries(hs2_asset_loading_checks PRIVATE hs2_scene)
    target_compile_options(hs2_asset_loading_checks PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/utf-8>)
    add_test(NAME hs2_asset_loading_checks COMMAND hs2_asset_loading_checks)
    add_executable(hs2_block_cache_checks ../../tests/HS2Manager.CppSceneChecks/UnityBlockCacheChecks.cpp)
    target_link_libraries(hs2_block_cache_checks PRIVATE hs2_scene)
    target_compile_options(hs2_block_cache_checks PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/utf-8>)
    add_test(NAME hs2_block_cache_checks COMMAND hs2_block_cache_checks)
endif()
