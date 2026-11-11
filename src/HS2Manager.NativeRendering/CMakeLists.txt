cmake_minimum_required(VERSION 3.24)
if(CMAKE_SOURCE_DIR STREQUAL CMAKE_CURRENT_SOURCE_DIR)
    project(HS2ManagerNativeRendering LANGUAGES C CXX)
    enable_testing()
    add_subdirectory(../HS2Manager.NativeScene ${CMAKE_CURRENT_BINARY_DIR}/scene)
endif()
add_library(
    hs2_rendering STATIC
    Device/GraphicsDevice.cpp
    Device/GpuResources.cpp
    Shaders/ShaderMetadata.cpp
    Shaders/ShaderReader.cpp
    Materials/NativeMaterialPass.cpp
    Geometry/NativeGpuMeshDeformer.cpp
    PostProcessing/NativeFullscreenPass.cpp
    Materials/GpuMaterialComposer.cpp
    Materials/GpuCharacterAppearance.cpp
    Lighting/NativeSkybox.cpp
    Lighting/NativeDirectionalShadows.cpp
    Pipeline/FrameBindings.cpp
    Pipeline/NativeDeferredRenderer.cpp
    Pipeline/NativeForwardRenderer.cpp
    PostProcessing/NativePostSettings.cpp
    PostProcessing/NativePostProcessor.cpp
    PostProcessing/NativeAntialiasing.cpp
    PostProcessing/NativeMotion.cpp
    PostProcessing/NativeScreenEffects.cpp
    Pipeline/NativeFramePipeline.cpp
)
target_compile_features(hs2_rendering PUBLIC cxx_std_20)
target_compile_definitions(hs2_rendering PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN)
target_include_directories(hs2_rendering PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})
target_link_libraries(hs2_rendering PUBLIC hs2_scene d3d11 dxgi d3dcompiler windowscodecs ole32)
if(MSVC)
    target_compile_options(hs2_rendering PRIVATE /W4 /permissive- /utf-8 /EHsc)
endif()
add_executable(hs2_rendering_checks ../../tests/HS2Manager.CppRenderingChecks/RenderingChecks.cpp)
target_link_libraries(hs2_rendering_checks PRIVATE hs2_rendering)
target_compile_options(hs2_rendering_checks PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/utf-8>)
add_test(NAME cpp_rendering COMMAND hs2_rendering_checks)
add_executable(
    hs2_render_definition_checks ../../tests/HS2Manager.CppRenderingChecks/RenderDefinition.cpp
                                 ../../tests/HS2Manager.CppRenderingChecks/FrozenDefinition.cpp
)
target_compile_options(hs2_render_definition_checks PRIVATE /utf-8 /W4 /EHsc)
target_link_libraries(hs2_render_definition_checks PRIVATE hs2_rendering)
if(TARGET hs2_character)
    add_executable(hs2_render_frame_checks ../../tests/HS2Manager.CppRenderingChecks/RenderFrame.cpp)
    target_compile_options(hs2_render_frame_checks PRIVATE /utf-8 /W4 /EHsc)
    target_link_libraries(hs2_render_frame_checks PRIVATE hs2_rendering hs2_character)
endif()
