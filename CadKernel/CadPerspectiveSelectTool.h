#pragma once
#include "CadLayoutTools.h"
#include "CadPerspectivePoints.h"
#include "Core/Entity/PointEntity.hpp"
#include "Document/Command/CopyCommand.h"
#include "Document/Command/MoveCommand.h"

class CadPerspectiveSelectTool : public MiniCAD::ITool
{
public:
    enum class Operation { Inspect, Move, Copy };
    CadPerspectiveSelectTool(uint64_t layout, Operation operation) : m_layout(layout), m_operation(operation) {}
    bool HandlesNavigation() const override { return true; }
    bool HasAnchor() const override { return m_base.has_value(); }
    MiniCAD::Math::Point3 GetAnchor() const override { return m_base.value_or(Point{}); }
    std::string GetPrompt() const override
    {
        if (!m_issue.empty()) return m_issue;
        if (!m_viewport) return "单击要查看的透视视口内部，Esc 退出";
        if (!m_selected) return "选取模型点、直线、圆、圆弧、椭圆或多段线，Esc 退出";
        if (m_operation != Operation::Inspect)
        {
            if (!m_base) return "指定模型基点，Esc 取消";
            return m_operation == Operation::Copy ? "指定复制目标点，可连续复制，Enter／空格完成" : "指定移动目标点，Esc 取消";
        }
        return "可在属性面板修改选中对象，单击切换对象，Esc 退出";
    }

    std::optional<uint64_t> PropertySelection(const MiniCAD::Scene& scene) const override
    {
        if (m_operation != Operation::Inspect) return std::nullopt;
        const auto* view = View(scene);
        return view && Selected(scene, *view) ? m_selected : 0;
    }

    bool OnInput(const MiniCAD::EditorContext& ctx) override
    {
        using namespace MiniCAD;
        m_overlay = &ctx.overlay;
        const auto& event = ctx.event;
        if (!ctx.scene.CurrentSpaceIsPaper() || ctx.scene.CurrentLayoutKey() != m_layout ||
            event.IsCancel() || event.IsRightClick() || event.IsKeyPressed(KeyCode::Enter) || event.IsKeyPressed(KeyCode::Space))
        {
            Cancel(); return true;
        }
        const bool click = event.IsLeftClick();
        if (!click && (event.Type != InputEventType::MouseMove || event.IsMiddleDrag())) return false;
        const auto& camera = ctx.viewport.GetCamera();
        const auto paper = camera.ScreenToWorld(event.MouseX, event.MouseY);
        if (!m_viewport)
        {
            if (click && !event.HasSnap)
            {
                const auto* picked = FindPaperViewport(ctx.scene, m_layout, paper, true);
                if (picked && picked->ClipKnown() && PerspectiveViewport(*picked).Valid()) m_viewport = picked->GetID();
            }
            return true;
        }
        const auto* view = View(ctx.scene);
        if (!view) { Cancel(); return true; }
        if (m_selected && m_operation != Operation::Inspect)
            return Transfer(ctx, *view, paper, click);
        if (!click || event.HasSnap) return true;
        const auto id = view->ContainsPaper(paper) ? Pick(ctx.scene, *view, paper, camera.GetWorldPerPixel()) : 0;
        if (m_operation != Operation::Inspect)
        {
            if (const auto* entity = dynamic_cast<const Entity*>(ctx.scene.GetEntity(id)); entity && ctx.scene.IsEntityLocked(*entity))
            { m_issue = "对象所在图层已锁定，请选择其他对象"; return true; }
            m_issue.clear();
        }
        if (id != m_selected)
        {
            m_selected = id;
            ctx.picking.MarkDirty();
        }
        return true;
    }

    void RefreshOverlay(const MiniCAD::Scene& scene, MiniCAD::Overlay& overlay) const override
    {
        using namespace MiniCAD;
        overlay.Clear();
        const auto* view = View(scene);
        if (!view) return;
        const auto* object = Selected(scene, *view);
        if (!object) return;
        const PerspectiveViewport projection(*view);
        if (!projection.Valid()) return;
        const auto boundary = PerspectivePaperBoundary(*view);
        Render(*object, *view, projection, boundary, {}, {0.35f, 0.85f, 1, 1}, overlay);
        if (m_target) overlay.AddPoint(m_target->paper, {0.45f, 1, 0.6f, 1});
        if (m_target && m_base)
            Render(*object, *view, projection, boundary, m_target->model - *m_base, {0.45f, 1, 0.6f, 1}, overlay);
    }

    void OnFocusLost() override { m_target.reset(); }

    void Cancel() override
    {
        m_selected = 0;
        m_base.reset();
        m_target.reset();
        if (m_overlay) m_overlay->Clear();
        if (OnFinished) OnFinished();
    }

private:
    using Point = MiniCAD::Math::Point3;

    bool Transfer(const MiniCAD::EditorContext& ctx, const MiniCAD::ViewportEntity& view, const Point& paper, bool click)
    {
        using namespace MiniCAD;
        const auto* entity = Selected(ctx.scene, view);
        if (!entity || ctx.scene.IsEntityLocked(*entity))
        {
            m_selected = 0;
            m_base.reset();
            m_target.reset();
            m_issue = "对象已隐藏或锁定，请重新选择";
            return true;
        }
        auto plane = ctx.scene.GetWorkingPlane();
        if (m_base) plane.Origin = *m_base;
        const PerspectiveViewport projection(view);
        m_target = m_points.Resolve(ctx, view, projection, paper, plane);
        if (!m_target)
        {
            if (click) m_issue = "请在视口可见范围内指定点或捕捉模型特征点";
            return true;
        }
        m_issue.clear();
        if (!click) return true;
        const auto target = m_target->model;
        m_target.reset();
        if (!m_base) { m_base = target; return true; }
        const auto delta = target - *m_base;
        if (delta.Length() <= Math::LengthEPS) { m_issue = "目标点与基点重合，请重新指定"; return true; }
        std::vector<Object::ObjectID> ids{m_selected};
        if (m_operation == Operation::Copy)
            ctx.cmdStack.Execute(std::make_unique<CopyCommand>(std::move(ids), delta), ctx.scene);
        else if (ctx.cmdStack.Execute(std::make_unique<MoveCommand>(ids, delta, ctx.scene), ctx.scene))
            Cancel();
        return true;
    }

    const MiniCAD::Entity* Selected(const MiniCAD::Scene& scene, const MiniCAD::ViewportEntity& view) const
    {
        const auto* entity = dynamic_cast<const MiniCAD::Entity*>(scene.GetEntity(m_selected));
        return entity && IsVisibleThroughViewport(*entity, scene, view) ? entity : nullptr;
    }

    static void Render(const MiniCAD::Entity& object, const MiniCAD::ViewportEntity& view,
                       const MiniCAD::PerspectiveViewport& projection, const MiniCAD::WipeClip::Poly& boundary,
                       const MiniCAD::Math::Vec3& delta, const MiniCAD::Math::Color4& color, MiniCAD::Overlay& overlay)
    {
        using namespace MiniCAD;
        if (const auto* node = dynamic_cast<const PointEntity*>(&object))
        {
            const auto paper = projection.ModelPointToPaper(node->GetPoint().Position + delta);
            if (paper && view.ContainsPaper(*paper)) overlay.AddPoint(*paper, color);
            return;
        }
        ForEachPerspectiveCurveSegment(object, view, projection, boundary, delta,
            [&](const Point& a, const Point& b) { overlay.AddLine(a, b, color); });
    }

    const MiniCAD::ViewportEntity* View(const MiniCAD::Scene& scene) const
    {
        const auto* view = dynamic_cast<const MiniCAD::ViewportEntity*>(scene.GetEntity(m_viewport));
        if (!view || !scene.CurrentSpaceIsPaper() || scene.CurrentLayoutKey() != m_layout ||
            !view->GetAttr().InPaperSpace || view->GetAttr().LayoutKey != m_layout ||
            !view->GetAttr().Visible || !view->ShowsModel() || !view->IsPerspective() || !view->ClipKnown()) return nullptr;
        const auto* layer = scene.GetLayerManager().GetLayer(view->GetLayerID());
        return !layer || layer->IsVisible() ? view : nullptr;
    }

    static uint64_t Pick(const MiniCAD::Scene& scene, const MiniCAD::ViewportEntity& view, const Point& paper, double pixel)
    {
        using namespace MiniCAD;
        const PerspectiveViewport projection(view);
        if (!projection.Valid()) return 0;
        const auto boundary = PerspectivePaperBoundary(view);
        const Point hit{paper.x, paper.y, view.GetCenter().z};
        const double radius = 6 * pixel;
        uint64_t pointId = 0, curveId = 0;
        double bestPoint = radius * radius, bestCurve = bestPoint;
        scene.ForEachObject([&](const Object& object)
        {
            const auto* entity = dynamic_cast<const Entity*>(&object);
            if (!entity || !IsVisibleThroughViewport(*entity, scene, view)) return;
            if (const auto* node = dynamic_cast<const PointEntity*>(entity))
            {
                const auto candidate = projection.ModelPointToPaper(node->GetPoint().Position);
                if (!candidate || !view.ContainsPaper(*candidate)) return;
                const double distance = (*candidate - hit).LengthSq();
                if (distance < bestPoint) { bestPoint = distance; pointId = entity->GetID(); }
            }
            else if (entity->AsCurveEntity() && projection.BoundsMayHitPaper(entity->GetBoundingBox(), hit, radius))
            {
                ForEachPerspectiveCurveSegment(*entity, view, projection, boundary, {}, [&](const Point& a, const Point& b)
                {
                    const auto v = b - a;
                    const double length = v.LengthSq();
                    const double t = length > 0 ? std::clamp(Math::Dot(hit - a, v) / length, 0.0, 1.0) : 0;
                    const double distance = (hit - (a + v * t)).LengthSq();
                    if (distance < bestCurve) { bestCurve = distance; curveId = entity->GetID(); }
                });
            }
        });
        return pointId && std::sqrt(bestPoint) <= std::sqrt(bestCurve) + pixel ? pointId : curveId;
    }

    uint64_t m_layout, m_viewport = 0, m_selected = 0;
    Operation m_operation;
    CadPerspectivePoints m_points;
    std::optional<Point> m_base;
    std::optional<CadPerspectivePoints::Resolved> m_target;
    std::string m_issue;
    MiniCAD::Overlay* m_overlay = nullptr;
};
