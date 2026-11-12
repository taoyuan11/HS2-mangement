cmake_minimum_required(VERSION 3.24)
project(
    HS2ManagerNative
    VERSION 1.4.0
    LANGUAGES C CXX
)
if(NOT WIN32 OR NOT MSVC)
    message(FATAL_ERROR "HS2 Manager requires Windows and the Visual Studio C++ toolchain.")
endif()
include(CTest)
add_subdirectory(src/HS2Manager.NativeScene)
add_subdirectory(src/HS2Manager.NativeCore)
include(src/HS2Manager.NativeScene/Character.cmake)
add_subdirectory(src/HS2Manager.NativeRendering)
add_subdirectory(src/HS2Manager.NativeExport)
add_subdirectory(src/HS2Manager.NativeAssetBuild)
add_subdirectory(src/HS2Manager.NativeAgent)
add_library(hs2_startup STATIC src/HS2Manager.NativeApp/Infrastructure/DataLocation.cpp)
add_library(hs2_process_lifetime STATIC src/HS2Manager.NativeApp/Infrastructure/ProcessLifetime.cpp)
target_include_directories(hs2_process_lifetime PUBLIC src/HS2Manager.NativeApp)
target_compile_features(hs2_process_lifetime PUBLIC cxx_std_20)
target_compile_definitions(hs2_process_lifetime PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_compile_options(hs2_process_lifetime PRIVATE /utf-8 /W4 /EHsc)
target_link_libraries(hs2_startup PUBLIC hs2_core mpr winsqlite3)
target_include_directories(hs2_startup PUBLIC src/HS2Manager.NativeApp)
target_compile_options(hs2_startup PRIVATE /utf-8 /W4 /EHsc)
target_compile_definitions(hs2_startup PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
if(BUILD_TESTING)
    add_executable(hs2_temporary_preview_checks tests/HS2Manager.CppCoreChecks/TemporaryPreviewChecks.cpp)
    target_link_libraries(hs2_temporary_preview_checks PRIVATE hs2_character)
    target_compile_options(hs2_temporary_preview_checks PRIVATE /utf-8 /W4 /EHsc)
    target_link_options(
        hs2_temporary_preview_checks PRIVATE "/MANIFEST:EMBED"
        "/MANIFESTINPUT:${CMAKE_SOURCE_DIR}/tests/HS2Manager.CppCoreChecks/LongPaths.manifest"
    )
    add_test(
        NAME temporary_preview
        COMMAND
            hs2_temporary_preview_checks "${HS2_SEVENZIP_ROOT}/source/CPP/7zip/Bundles/Format7zF/x64/7z.dll"
            "${CMAKE_SOURCE_DIR}/tests/fixtures/temporary-preview"
            "${CMAKE_SOURCE_DIR}/src/HS2Manager.NativeApp/Assets/Brand/hs2-manager.png"
            "${CMAKE_SOURCE_DIR}/src/HS2Manager.NativeScene/Resources/classdata.tpk"
    )
    add_subdirectory(tests/HS2Manager.CppToolChecks)
    add_library(hs2_exit_blocking_fixture SHARED tests/HS2Manager.CppUiChecks/ExitBlockingDll.cpp)
    target_compile_options(hs2_exit_blocking_fixture PRIVATE /utf-8 /W4)
    add_executable(hs2_process_lifetime_checks tests/HS2Manager.CppUiChecks/ProcessLifetimeChecks.cpp)
    target_link_libraries(hs2_process_lifetime_checks PRIVATE hs2_process_lifetime hs2_export_tools_checks_impl)
    target_compile_options(hs2_process_lifetime_checks PRIVATE /utf-8 /W4 /EHsc)
    target_compile_definitions(hs2_process_lifetime_checks PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
    add_test(NAME process_exit_lifetime COMMAND hs2_process_lifetime_checks "$<TARGET_FILE:hs2_exit_blocking_fixture>"
                                                "$<TARGET_FILE:hs2_tool_fixture>"
    )
    set_tests_properties(process_exit_lifetime PROPERTIES TIMEOUT 60)
    add_executable(
        hs2_preview_worker_checks tests/HS2Manager.CppUiChecks/PreviewWorkerChecks.cpp
                                  src/HS2Manager.NativeApp/Preview/NativePreviewWorker.cpp
    )
    target_include_directories(hs2_preview_worker_checks PRIVATE src/HS2Manager.NativeApp)
    target_link_libraries(hs2_preview_worker_checks PRIVATE hs2_character hs2_rendering)
    target_compile_options(hs2_preview_worker_checks PRIVATE /utf-8 /W4 /EHsc)
    add_test(NAME preview_worker_lifetime COMMAND hs2_preview_worker_checks)
    set_tests_properties(preview_worker_lifetime PROPERTIES TIMEOUT 15)
    add_executable(
        hs2_preview_visibility_checks tests/HS2Manager.CppUiChecks/PreviewVisibilityChecks.cpp
                                      src/HS2Manager.NativeApp/Preview/NativePreviewWorker.cpp
    )
    target_include_directories(hs2_preview_visibility_checks PRIVATE src/HS2Manager.NativeApp)
    target_link_libraries(hs2_preview_visibility_checks PRIVATE hs2_character hs2_rendering)
    target_compile_options(hs2_preview_visibility_checks PRIVATE /utf-8 /W4 /EHsc)
    add_executable(hs2_startup_checks tests/HS2Manager.CppUiChecks/DataLocationChecks.cpp)
    target_link_libraries(hs2_startup_checks PRIVATE hs2_startup)
    target_compile_options(hs2_startup_checks PRIVATE /utf-8 /W4 /EHsc)
    add_test(NAME native_startup COMMAND hs2_startup_checks)
    add_executable(hs2_startup_recovery_checks tests/HS2Manager.CppUiChecks/StartupRecoveryChecks.cpp)
    target_link_libraries(hs2_startup_recovery_checks PRIVATE hs2_startup)
    target_compile_options(hs2_startup_recovery_checks PRIVATE /utf-8 /W4 /EHsc)
    add_test(NAME native_startup_recovery COMMAND hs2_startup_recovery_checks)
    add_executable(hs2_ui_fixtures tests/HS2Manager.CppUiChecks/Fixtures.cpp)
    target_link_libraries(hs2_ui_fixtures PRIVATE hs2_core)
    target_compile_options(hs2_ui_fixtures PRIVATE /utf-8 /W4 /EHsc)
endif()
