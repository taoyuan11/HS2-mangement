cmake_minimum_required(VERSION 3.24)
if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    project(HS2ManagerNativeExport LANGUAGES CXX)
    enable_testing()
endif()
add_library(hs2_export STATIC Fbx/FbxScene.cpp)
target_compile_features(hs2_export PUBLIC cxx_std_20)
target_include_directories(hs2_export PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_compile_definitions(hs2_export PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN)
if(MSVC)
    target_compile_options(hs2_export PRIVATE /W4 /permissive- /utf-8 /EHsc)
endif()
add_executable(hs2_export_checks ../../tests/HS2Manager.CppRenderingChecks/ExportChecks.cpp)
target_link_libraries(hs2_export_checks PRIVATE hs2_export)
target_compile_options(hs2_export_checks PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/utf-8>)
set(HS2_FBX_BRIDGE
    ""
    CACHE FILEPATH "Built HS2Manager.FbxBridge.dll for native SDK roundtrip checks"
)
if(HS2_FBX_BRIDGE)
    add_test(NAME cpp_fbx_roundtrip COMMAND hs2_export_checks "${HS2_FBX_BRIDGE}")
endif()
if(TARGET hs2_core AND TARGET hs2_rendering)
    file(READ "${CMAKE_CURRENT_SOURCE_DIR}/Baking/NativePbrBake.hlsl" PBR_SHADER_SOURCE)
    configure_file(Baking/PbrShader.h.in "${CMAKE_CURRENT_BINARY_DIR}/PbrShader.h" @ONLY)
    add_library(
        hs2_model_export STATIC
        Baking/ExportGpuPass.cpp
        Images/ImageFiles.cpp
        Baking/NativePbrBake.cpp
        Model/NativeCharacterExport.cpp
        Model/NativeSceneExport.cpp
        Model/ModelExport.cpp
        Tools/ModelExportTools.cpp
        Tools/OwnedToolProcess.cpp
    )
    target_compile_features(hs2_model_export PUBLIC cxx_std_20)
    target_compile_options(hs2_model_export PRIVATE /W4 /permissive- /utf-8 /EHsc)
    target_include_directories(
        hs2_model_export
        PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}"
        PRIVATE "${CMAKE_CURRENT_BINARY_DIR}"
    )
    target_link_libraries(
        hs2_model_export
        PUBLIC hs2_export
               hs2_rendering
               hs2_core
               hs2_card_export
               hs2_character
               advapi32
               shell32
               ole32
    )
    add_executable(hs2_model_export_checks ../../tests/HS2Manager.CppRenderingChecks/ModelExportChecks.cpp)
    target_link_libraries(hs2_model_export_checks PRIVATE hs2_model_export)
    target_compile_options(hs2_model_export_checks PRIVATE /utf-8 /EHsc)
    add_test(NAME cpp_model_export COMMAND hs2_model_export_checks)
endif()
