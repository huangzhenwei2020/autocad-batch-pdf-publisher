#pragma once
#include "CadPerspectiveGeometry.h"
#include "Document/FeaturePoints.h"
#include "Editor/EditorContext.h"
#include <array>
#include <optional>

class CadPerspectivePoints
{
    using Point = MiniCAD::Math::Point3;
    struct Feature { Point paper, model; };

public:
    struct Resolved { Point model, paper; bool snapped; };

    std::optional<Resolved> Resolve(const MiniCAD::EditorContext& ctx, const MiniCAD::ViewportEntity& view,
                                 const MiniCAD::PerspectiveViewport& camera, const Point& paper,
                                 const MiniCAD::Math::WorkingPlane& plane)
    {
        if (!ctx.event.HasSnap && !view.ContainsPaper(paper)) return {};
        const auto snap = ctx.event.HasSnap ? std::optional{ctx.event.SnapWorld} : ResolveSnap(ctx, view, camera, paper);
        const auto point = snap ? snap : camera.PaperPointOnPlane(paper, plane);
        const auto projected = point ? camera.ModelPointToPaper(*point) : std::nullopt;
        if (!projected || !view.ContainsPaper(*projected)) return {};
        return Resolved{*point, *projected, snap.has_value()};
    }

private:
    std::optional<Point> ResolveSnap(const MiniCAD::EditorContext& ctx, const MiniCAD::ViewportEntity& view,
                                    const MiniCAD::PerspectiveViewport& camera, const Point& paper)
    {
        using namespace MiniCAD;
        if (!ctx.snap.IsEnabled()) return {};
        const auto version = std::pair{ctx.scene.GeometryVersion(), ctx.scene.DisplayVersion()};
        const auto modes = ctx.snap.GetSnapModes();
        if (m_cacheVersion != version || m_modes != modes)
        {
            const std::array kinds{std::pair{FeatureKind::Endpoint, SnapMode::Endpoint},
                                   std::pair{FeatureKind::Midpoint, SnapMode::Midpoint},
                                   std::pair{FeatureKind::Quadrant, SnapMode::Quadrant}};
            m_features.clear();
            std::vector<Point> points;
            ctx.scene.ForEachObject([&](const Object& object)
            {
                const auto* entity = dynamic_cast<const Entity*>(&object);
                if (!entity || !IsVisibleThroughViewport(*entity, ctx.scene, view)) return;
                points.clear();
                for (const auto [kind, mode] : kinds)
                    if (ctx.snap.IsModeEnabled(mode)) CollectFeaturePoints(*entity, kind, points);
                for (const auto& point : points)
                    if (const auto projected = camera.ModelPointToPaper(point); projected && view.ContainsPaper(*projected))
                        m_features.push_back({*projected, point});
            });
            std::stable_sort(m_features.begin(), m_features.end(), [](const Feature& a, const Feature& b) { return a.paper.x < b.paper.x; });
            m_cacheVersion = version;
            m_modes = modes;
        }
        const double radius = ctx.snap.GetSnapRadiusPx() * ctx.viewport.GetCamera().GetWorldPerPixel();
        auto item = std::lower_bound(m_features.begin(), m_features.end(), paper.x - radius,
            [](const Feature& feature, double x) { return feature.paper.x < x; });
        std::optional<Point> result;
        double best = radius * radius;
        for (; item != m_features.end() && item->paper.x <= paper.x + radius; ++item)
        {
            const double distance = (item->paper - paper).LengthSq();
            if (distance < best) { best = distance; result = item->model; }
        }
        return result;
    }

    std::optional<std::pair<uint64_t, uint64_t>> m_cacheVersion;
    uint32_t m_modes = 0;
    std::vector<Feature> m_features;
};
