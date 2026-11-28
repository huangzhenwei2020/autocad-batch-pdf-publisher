#pragma once
#include "Document/PerspectiveViewport.hpp"
#include "Core/Entity/ICurveEntity.hpp"
#include "Core/Entity/WipeoutEntity.hpp"
#include "Scene/Scene.h"

inline bool IsVisibleThroughViewport(const MiniCAD::Entity& entity, const MiniCAD::Scene& scene,
    const MiniCAD::ViewportEntity& viewport)
{
    const auto& attr = entity.GetAttr();
    if (attr.InPaperSpace || !attr.Visible || viewport.IsLayerFrozen(attr.LayerId)) return false;
    const auto* layer = scene.GetLayerManager().GetLayer(attr.LayerId);
    return !layer || layer->IsVisible();
}

inline MiniCAD::WipeClip::Poly PerspectivePaperBoundary(const MiniCAD::ViewportEntity& view)
{
    auto boundary = view.PaperBoundary();
    const auto& origin = view.GetCenter();
    for (auto& point : boundary) point = MiniCAD::Math::Point3{} + (point - origin);
    return MiniCAD::WipeClip::Poly(boundary);
}

template<typename Emit>
void ForEachPerspectiveSegment(const MiniCAD::ViewportEntity& view, const MiniCAD::PerspectiveViewport& projection,
    const MiniCAD::WipeClip::Poly& boundary, const MiniCAD::Math::Point3& start, const MiniCAD::Math::Point3& end, Emit emit)
{
    using namespace MiniCAD;
    const auto& origin = view.GetCenter();
    Vertex_P3_C4 a{ToRenderFloat3(start, view.GetViewCenter()), {}};
    Vertex_P3_C4 b{ToRenderFloat3(end, view.GetViewCenter()), {}};
    if (!projection.Line(a, b, origin)) return;
    std::vector<Vertex_P3_C4> vertices{a, b};
    WipeClip::ClipLines(vertices, boundary, 0, true);
    auto toPaper = [&](const auto& vertex) { return origin + Math::Vec3{vertex.pos.x, vertex.pos.y, vertex.pos.z}; };
    for (size_t i = 1; i < vertices.size(); i += 2)
        emit(toPaper(vertices[i - 1]), toPaper(vertices[i]));
}

template<typename Emit>
void ForEachPerspectiveCurveSegment(const MiniCAD::Entity& entity, const MiniCAD::ViewportEntity& view,
    const MiniCAD::PerspectiveViewport& projection, const MiniCAD::WipeClip::Poly& boundary,
    const MiniCAD::Math::Vec3& delta, Emit emit)
{
    const auto* source = entity.AsCurveEntity();
    if (!source || entity.IsKindOf<MiniCAD::WipeoutEntity>()) return;
    const auto curve = source->MakeCurve();
    if (!curve || !curve->IsBounded()) return;
    std::vector<MiniCAD::Math::Point3> points;
    curve->Tessellate(points, 128);
    for (size_t i = 1; i < points.size(); ++i)
        ForEachPerspectiveSegment(view, projection, boundary, points[i - 1] + delta, points[i] + delta, emit);
}
