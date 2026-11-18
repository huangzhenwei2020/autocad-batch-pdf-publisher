#pragma once
#include "Document/Document.h"
#include "Editor/Constraint/ConstraintEngine.h"
#include "Viewport/Camera.h"
#include <stdexcept>

inline void CheckWorkingPlane()
{
    using namespace MiniCAD;
    auto check = [](bool condition) { if (!condition) throw std::runtime_error("Working plane invariant failed"); };
    Math::WorkingPlane plane;
    check(plane.Set({100,200,300}, {2,0,0}, {1,0,3}));
    const auto world = plane.ToWorld({50,25,0});
    check(std::hypot(world.x - 150, world.y - 200, world.z - 325) < 1e-9);
    const auto local = plane.ToLocal(world);
    check(std::hypot(local.x - 50, local.y - 25, local.z) < 1e-9);
    auto invalid = plane;
    check(!invalid.Set({}, {1,0,0}, {2,0,0}));
    check(invalid.Origin.z == 300);
    Camera camera(1000,700);
    camera.SetStandardView(StandardView::Front);
    auto state = camera.GetState(); state.Target = {100,200,300}; camera.SetState(state);
    Math::Point3 hit;
    check(camera.ScreenToPlane(500,350,plane,hit));
    check(std::hypot(hit.x - 100, hit.y - 200, hit.z - 300) < 1e-9);
    check(!camera.ScreenToPlane(500,350,{},hit));
    for (const auto view : {StandardView::Back, StandardView::Left, StandardView::Right, StandardView::Bottom, StandardView::SWIso})
    {
        camera.SetStandardView(view);
        Math::WorkingPlane observed;
        check(observed.Set({100,200,300}, camera.GetRight(), camera.GetUp()));
        const auto expected = observed.ToWorld({25,50,0});
        const auto screen = camera.WorldToScreen(expected);
        check(camera.ScreenToPlane(screen.x, screen.y, observed, hit));
        check(std::hypot(hit.x - expected.x, hit.y - expected.y, hit.z - expected.z) < 1e-7);
    }
    ConstraintEngine constraints;
    constraints.EnableOrtho(true);
    ConstraintContext context;
    context.anchor = {100,200,300}; context.input = {110,200,350};
    check(constraints.Apply(context, plane));
    hit = constraints.GetConstrainedPoint();
    check(std::hypot(hit.x - 100, hit.y - 200, hit.z - 350) < 1e-9);
    constraints.EnablePolar(true);
    constraints.GetPolar().SetAngleDeg(30);
    context.input = {140,200,322};
    check(constraints.Apply(context, plane));
    hit = plane.ToLocal(constraints.GetConstrainedPoint());
    check(std::abs(std::atan2(hit.y, hit.x) - Math::PI / 6) < 1e-9);
    Document document;
    document.GetScene().SetWorkingPlane(plane);
    Document restored;
    check(restored.LoadFromString(document.SaveToString()));
    check(restored.GetScene().GetWorkingPlane().Origin.z == 300);
    check(restored.GetScene().GetWorkingPlane().Y.z == 1);
}
