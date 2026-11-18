cmake_minimum_required(VERSION 3.20)
project(WanluoCadFeatureFixtures LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_executable(WanluoCadFeatureFixtures FixtureGenerator.cpp)
target_compile_options(WanluoCadFeatureFixtures PRIVATE /utf-8 /bigobj)
target_include_directories(WanluoCadFeatureFixtures PRIVATE
    "${WANLUO_REPO}/ThirdParty/MiniCAD/src" "${WANLUO_REPO}/ThirdParty/MiniCAD/src/I18n" "${WANLUO_REPO}/ThirdParty/MiniCAD/3rd"
    "${WANLUO_REPO}/ThirdParty/MiniCAD/src/Serialization")
target_link_libraries(WanluoCadFeatureFixtures PRIVATE
    "${WANLUO_REPO}/.artifacts/cad-kernel/MiniCAD/Release/MiniCAD.lib"
    "${WANLUO_REPO}/.artifacts/cad-kernel/MiniCAD/Release/MiniDWG.lib"
    "${WANLUO_REPO}/.artifacts/cad-kernel/MiniCAD/Release/MiniI18n.lib")
