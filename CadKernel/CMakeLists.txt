cmake_minimum_required(VERSION 3.20)
project(WanluoCadKernel LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(WANLUO_BUILD_CAD_CORE ON CACHE BOOL "" FORCE)
add_subdirectory("../ThirdParty/MiniCAD" MiniCAD)
set(RENDER "${CMAKE_CURRENT_SOURCE_DIR}/../ThirdParty/MiniCAD/apps/Render")
add_library(WanluoCadKernel SHARED CadKernel.cpp
    "${RENDER}/D3D11/Device.cpp"
    "${RENDER}/D3D11/SwapChain.cpp"
    "${RENDER}/D3D11/Shader.cpp"
    "${RENDER}/D3D11/D3D11RenderTarget.cpp"
    "${RENDER}/D3D11/D3D11Renderer.cpp")
target_include_directories(WanluoCadKernel PRIVATE "${RENDER}")
target_link_libraries(WanluoCadKernel PRIVATE MiniCAD d3d11 dxgi D3DCompiler user32)
target_compile_options(WanluoCadKernel PRIVATE /utf-8 /W4 /MP4 /FI"${RENDER}/D3D11/D3D11Common.h")
target_compile_definitions(WanluoCadKernel PRIVATE UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
