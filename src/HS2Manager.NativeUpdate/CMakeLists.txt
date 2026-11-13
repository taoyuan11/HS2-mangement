set(update_sources Update.cpp Network.cpp Transaction.cpp)
add_library(hs2_update STATIC ${update_sources})
target_include_directories(
    hs2_update
    PUBLIC . "${CMAKE_BINARY_DIR}/generated"
    PRIVATE ../HS2Manager.NativeCore ../HS2Manager.NativeScene
)
target_compile_features(hs2_update PUBLIC cxx_std_20)
target_compile_definitions(hs2_update PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN UNICODE _UNICODE)
target_compile_options(hs2_update PRIVATE /utf-8 /W4 /EHsc)
target_link_libraries(hs2_update PUBLIC winhttp bcrypt shell32 hs2_archive)

# 更新器单独编译 ZIP 与更新代码，静态 CRT 避免占用待替换的运行库。
add_executable(
    HS2Updater WIN32 Main.cpp ${update_sources} ../HS2Manager.NativeScene/Archive/ZipArchive.cpp
                     ../HS2Manager.NativeScene/ThirdParty/Miniz/miniz_tinfl.c
)
set_property(TARGET HS2Updater PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
target_include_directories(
    HS2Updater PRIVATE . ../HS2Manager.NativeCore ../HS2Manager.NativeScene "${CMAKE_BINARY_DIR}/generated"
)
target_compile_features(HS2Updater PRIVATE cxx_std_20)
target_compile_definitions(
    HS2Updater
    PRIVATE NOMINMAX
            WIN32_LEAN_AND_MEAN
            UNICODE
            _UNICODE
            MINIZ_NO_ARCHIVE_APIS
            MINIZ_NO_DEFLATE_APIS
            MINIZ_NO_ZLIB_APIS
)
target_compile_options(HS2Updater PRIVATE /utf-8 /W4 /EHsc)
target_link_libraries(HS2Updater PRIVATE winhttp bcrypt shell32)
if(BUILD_TESTING)
    add_executable(hs2_update_checks ../../tests/HS2Manager.CppUpdateChecks/Checks.cpp)
    target_compile_features(hs2_update_checks PRIVATE cxx_std_20)
    target_compile_options(hs2_update_checks PRIVATE /utf-8 /W4 /EHsc)
    target_include_directories(hs2_update_checks PRIVATE ../HS2Manager.NativeCore)
    target_link_libraries(hs2_update_checks PRIVATE hs2_update hs2_archive)
    add_test(NAME application_update COMMAND hs2_update_checks)
    set_tests_properties(application_update PROPERTIES TIMEOUT 60)
endif()
