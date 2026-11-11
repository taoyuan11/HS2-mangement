cmake_minimum_required(VERSION 3.24)
if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    project(HS2ManagerNativeScene LANGUAGES C CXX)
    enable_testing()
endif()
if(NOT TARGET hs2_archive)
    add_library(hs2_archive STATIC Archive/ZipArchive.cpp ThirdParty/Miniz/miniz_tinfl.c)
    target_compile_features(hs2_archive PUBLIC cxx_std_20)
    target_include_directories(hs2_archive PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
    target_compile_definitions(
        hs2_archive
        PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN
        PRIVATE MINIZ_NO_ARCHIVE_APIS MINIZ_NO_DEFLATE_APIS MINIZ_NO_ZLIB_APIS
    )
    if(MSVC)
        target_compile_options(hs2_archive PRIVATE /utf-8)
    endif()
endif()
add_library(
    hs2_scene STATIC
    Scene/SceneTypes.cpp
    Assets/AssetReader.cpp
    Assets/AssetStore.cpp
    Animation/Animation.cpp
    Animation/ShapeRig.cpp
    Character/CharacterAssembly.cpp
    Runtime/NativeDynamics.cpp
    Runtime/ClothRuntime.cpp
    Runtime/CharacterRuntime.cpp
    Animation/PoseControllers.cpp
    ThirdParty/Lzma/LzmaDec.c
)
target_compile_features(hs2_scene PUBLIC cxx_std_20)
target_include_directories(hs2_scene PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_definitions(hs2_scene PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN)
if(MSVC)
    target_compile_options(hs2_scene PRIVATE /W4 /utf-8 /fp:precise)
endif()
if(BUILD_TESTING OR CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
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
endif()
