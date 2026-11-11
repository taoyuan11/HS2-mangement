add_library(hs2_character STATIC
  "${CMAKE_CURRENT_LIST_DIR}/CharacterCatalog.cpp"
  "${CMAKE_CURRENT_LIST_DIR}/CharacterBodyCatalog.cpp"
  "${CMAKE_CURRENT_LIST_DIR}/CharacterShaders.cpp"
  "${CMAKE_CURRENT_LIST_DIR}/EnvironmentReader.cpp"
  "${CMAKE_CURRENT_LIST_DIR}/SceneXml.cpp"
  "${CMAKE_CURRENT_LIST_DIR}/CharacterExtensions.cpp"
  "${CMAKE_CURRENT_LIST_DIR}/CharacterPushUp.cpp"
  "${CMAKE_CURRENT_LIST_DIR}/MonoSchema.cpp"
  "${CMAKE_CURRENT_LIST_DIR}/CharacterPreparation.cpp")
target_compile_features(hs2_character PUBLIC cxx_std_20)
target_include_directories(hs2_character PUBLIC "${CMAKE_CURRENT_LIST_DIR}")
target_compile_options(hs2_character PRIVATE /utf-8 /W4 /EHsc /fp:precise)
target_link_libraries(hs2_character PUBLIC hs2_scene hs2_core hs2_archive hs2_core_components)
if(BUILD_TESTING)
  add_executable(hs2_character_checks "${CMAKE_CURRENT_LIST_DIR}/../../tests/HS2Manager.CppSceneChecks/CharacterChecks.cpp")
  target_compile_options(hs2_character_checks PRIVATE /utf-8 /W4 /EHsc)
  target_link_libraries(hs2_character_checks PRIVATE hs2_character)
  add_executable(hs2_extension_checks "${CMAKE_CURRENT_LIST_DIR}/../../tests/HS2Manager.CppSceneChecks/ExtensionChecks.cpp")
  target_compile_options(hs2_extension_checks PRIVATE /utf-8 /W4 /EHsc)
  target_link_libraries(hs2_extension_checks PRIVATE hs2_character)
  add_test(NAME cpp_extensions COMMAND hs2_extension_checks)
  add_executable(hs2_mono_schema_checks "${CMAKE_CURRENT_LIST_DIR}/../../tests/HS2Manager.CppSceneChecks/MonoSchemaChecks.cpp")
  target_compile_options(hs2_mono_schema_checks PRIVATE /utf-8 /W4 /EHsc)
  target_link_libraries(hs2_mono_schema_checks PRIVATE hs2_character)
  add_executable(hs2_environment_checks "${CMAKE_CURRENT_LIST_DIR}/../../tests/HS2Manager.CppSceneChecks/EnvironmentChecks.cpp")
  target_compile_options(hs2_environment_checks PRIVATE /utf-8 /W4 /EHsc)
  target_link_libraries(hs2_environment_checks PRIVATE hs2_character)
  add_executable(hs2_runtime_checks "${CMAKE_CURRENT_LIST_DIR}/../../tests/HS2Manager.CppSceneChecks/RuntimeChecks.cpp" "${CMAKE_CURRENT_LIST_DIR}/../../tests/HS2Manager.CppRenderingChecks/FrozenDefinition.cpp")
  target_compile_options(hs2_runtime_checks PRIVATE /utf-8 /W4 /EHsc)
  target_link_libraries(hs2_runtime_checks PRIVATE hs2_character)
endif()
