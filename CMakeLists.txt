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
add_library(hs2_startup STATIC src/HS2Manager.NativeApp/Infrastructure/DataLocation.cpp)
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
