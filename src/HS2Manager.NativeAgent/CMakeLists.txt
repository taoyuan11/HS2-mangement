add_library(
    hs2_agent STATIC
    Agent.cpp
    Support.cpp
    Render.cpp
    Assets.cpp
    Protocol.cpp
    ReferenceImages.cpp
    VisualReview.cpp
)
target_compile_features(hs2_agent PUBLIC cxx_std_20)
target_include_directories(hs2_agent PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_options(hs2_agent PRIVATE /utf-8 /W4 /EHsc)
target_link_libraries(hs2_agent PUBLIC hs2_creation crypt32 windowscodecs)
add_executable(HS2Agent Main.cpp)
target_compile_options(HS2Agent PRIVATE /utf-8 /W4 /EHsc)
target_link_libraries(HS2Agent PRIVATE hs2_agent hs2_update)
target_link_options(HS2Agent PRIVATE "/MANIFEST:EMBED" "/MANIFESTINPUT:${CMAKE_CURRENT_SOURCE_DIR}/app.manifest")
if(BUILD_TESTING)
    add_executable(hs2_agent_tool_runner ../../tests/HS2Manager.CppAgentChecks/OwnedToolRunner.cpp)
    target_compile_options(hs2_agent_tool_runner PRIVATE /utf-8 /W4 /EHsc)
    target_link_libraries(hs2_agent_tool_runner PRIVATE hs2_agent)
    add_executable(
        hs2_agent_checks ../../tests/HS2Manager.CppAgentChecks/Checks.cpp
                         ../../tests/HS2Manager.CppAgentChecks/QualityChecks.cpp
    )
    target_compile_options(hs2_agent_checks PRIVATE /utf-8 /W4 /EHsc)
    target_link_libraries(hs2_agent_checks PRIVATE hs2_agent)
    add_test(NAME agent_sessions COMMAND hs2_agent_checks
                                         "${CMAKE_SOURCE_DIR}/src/HS2Manager.NativeApp/Assets/Brand/hs2-manager.png"
    )
endif()
