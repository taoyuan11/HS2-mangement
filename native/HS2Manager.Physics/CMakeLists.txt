cmake_minimum_required(VERSION 3.24)
project(HS2ManagerPhysics LANGUAGES CXX)
set(PHYSX_ROOT "" CACHE PATH "Locally built NVIDIA PhysX 3.4.2 source root")
if(NOT EXISTS "${PHYSX_ROOT}/PhysX_3.4/Include/PxPhysicsVersion.h")
  message(FATAL_ERROR "PHYSX_ROOT must contain the NVIDIA PhysX 3.4.2 source distribution")
endif()
add_library(HS2Manager.Physics SHARED ClothBridge.cpp)
target_compile_features(HS2Manager.Physics PRIVATE cxx_std_17)
set_property(TARGET HS2Manager.Physics PROPERTY MSVC_RUNTIME_LIBRARY MultiThreaded)
target_include_directories(HS2Manager.Physics PRIVATE "${PHYSX_ROOT}/PhysX_3.4/Include" "${PHYSX_ROOT}/PxShared/include")
target_link_libraries(HS2Manager.Physics PRIVATE
  "${PHYSX_ROOT}/PhysX_3.4/Lib/vc15win64/PhysX3_x64.lib"
  "${PHYSX_ROOT}/PhysX_3.4/Lib/vc15win64/PhysX3Common_x64.lib"
  "${PHYSX_ROOT}/PhysX_3.4/Lib/vc15win64/PhysX3Extensions.lib"
  "${PHYSX_ROOT}/PxShared/lib/vc15win64/PxFoundation_x64.lib")
if(MSVC)
  target_compile_options(HS2Manager.Physics PRIVATE /W4 /EHsc /wd4996)
endif()
foreach(dll PhysX3_x64 PhysX3Common_x64)
  add_custom_command(TARGET HS2Manager.Physics POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${PHYSX_ROOT}/PhysX_3.4/Bin/vc15win64/${dll}.dll" "$<TARGET_FILE_DIR:HS2Manager.Physics>")
endforeach()
foreach(dll PxFoundation_x64 PxPvdSDK_x64)
  add_custom_command(TARGET HS2Manager.Physics POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${PHYSX_ROOT}/PxShared/bin/vc15win64/${dll}.dll" "$<TARGET_FILE_DIR:HS2Manager.Physics>")
endforeach()
