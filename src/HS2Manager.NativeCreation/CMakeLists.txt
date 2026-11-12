add_library(
    hs2_creation STATIC
    MeshDocument.cpp MeshTopology.cpp MeshSurface.cpp PaintDocument.cpp CreationProject.cpp CreationService.cpp
    CreationBuild.cpp CreationInstall.cpp CreationRender.cpp CreationIdentity.cpp CreationFit.cpp SharedAssets.cpp
)
target_compile_features(hs2_creation PUBLIC cxx_std_20)
target_include_directories(hs2_creation PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_options(hs2_creation PRIVATE /utf-8 /W4 /EHsc /bigobj)
target_link_libraries(hs2_creation PUBLIC hs2_asset_build)
if(BUILD_TESTING)
    add_executable(hs2_creation_checks ../../tests/HS2Manager.CppCreationChecks/Checks.cpp)
    target_compile_options(hs2_creation_checks PRIVATE /utf-8 /W4 /EHsc /bigobj)
    target_link_libraries(hs2_creation_checks PRIVATE hs2_creation hs2_card_export)
    target_link_options(hs2_creation_checks PRIVATE "/MANIFESTINPUT:${CMAKE_SOURCE_DIR}/src/HS2Manager.NativeAgent/app.manifest")
    add_test(NAME creation_projects COMMAND hs2_creation_checks
        "${CMAKE_SOURCE_DIR}/src/HS2Manager.NativeApp/Assets/Brand/hs2-manager.png")
    add_executable(hs2_creation_acceptance ../../tests/HS2Manager.CppCreationChecks/Acceptance.cpp)
    target_compile_options(hs2_creation_acceptance PRIVATE /utf-8 /W4 /EHsc /bigobj)
    target_link_libraries(hs2_creation_acceptance PRIVATE hs2_creation hs2_card_export)
    target_link_options(hs2_creation_acceptance PRIVATE "/MANIFESTINPUT:${CMAKE_SOURCE_DIR}/src/HS2Manager.NativeAgent/app.manifest")
    add_executable(hs2_creation_install_acceptance ../../tests/HS2Manager.CppCreationChecks/InstallationAcceptance.cpp)
    target_compile_options(hs2_creation_install_acceptance PRIVATE /utf-8 /W4 /EHsc /bigobj)
    target_link_libraries(hs2_creation_install_acceptance PRIVATE hs2_creation hs2_card_export)
    target_link_options(hs2_creation_install_acceptance PRIVATE "/MANIFESTINPUT:${CMAKE_SOURCE_DIR}/src/HS2Manager.NativeAgent/app.manifest")
    # Always relink validation executables: Windows tracking may omit transitive
    # static libraries, allowing an acceptance run to exercise an older builder.
    foreach(check hs2_creation_checks hs2_creation_acceptance hs2_creation_install_acceptance)
        set_property(TARGET ${check} PROPERTY VS_GLOBAL_Link_MinimalRebuildFromTracking false)
    endforeach()
endif()
