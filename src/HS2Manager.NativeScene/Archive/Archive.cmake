if(TARGET hs2_archive)
    return()
endif()
set(HS2_SEVENZIP_ROOT
    "$ENV{LOCALAPPDATA}/HS2Manager/NativeBuild/7zip-26.06"
    CACHE PATH "Pinned 7-Zip build directory"
)
file(TO_CMAKE_PATH "${HS2_SEVENZIP_ROOT}" HS2_SEVENZIP_ROOT)
set(HS2_SEVENZIP_DLL "${HS2_SEVENZIP_ROOT}/source/CPP/7zip/Bundles/Format7zF/x64/7z.dll")
if(NOT EXISTS "${HS2_SEVENZIP_ROOT}/complete-26.06.txt" OR NOT EXISTS "${HS2_SEVENZIP_DLL}")
    execute_process(
        COMMAND
            powershell -NoProfile -ExecutionPolicy Bypass -File
            "${CMAKE_CURRENT_LIST_DIR}/../../../scripts/build/Build-NativeArchive.ps1" -BuildDirectory
            "${HS2_SEVENZIP_ROOT}"
        RESULT_VARIABLE sevenzip_result
    )
    if(NOT sevenzip_result EQUAL 0)
        message(FATAL_ERROR "7-Zip 26.06 build failed")
    endif()
endif()
add_library(
    hs2_archive STATIC "${CMAKE_CURRENT_LIST_DIR}/ZipArchive.cpp" "${CMAKE_CURRENT_LIST_DIR}/PackageArchive.cpp"
                       "${CMAKE_CURRENT_LIST_DIR}/../ThirdParty/Miniz/miniz_tinfl.c"
)
target_compile_features(hs2_archive PUBLIC cxx_std_20)
target_include_directories(hs2_archive PUBLIC "${CMAKE_CURRENT_LIST_DIR}/..")
target_include_directories(hs2_archive SYSTEM PRIVATE "${HS2_SEVENZIP_ROOT}/source/CPP")
target_compile_definitions(
    hs2_archive
    PUBLIC NOMINMAX WIN32_LEAN_AND_MEAN
    PRIVATE MINIZ_NO_ARCHIVE_APIS MINIZ_NO_DEFLATE_APIS MINIZ_NO_ZLIB_APIS
)
target_compile_options(hs2_archive PRIVATE /utf-8 /EHsc)
target_link_libraries(hs2_archive PUBLIC ole32 oleaut32)
