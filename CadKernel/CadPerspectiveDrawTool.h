#pragma once
#include "CadLayoutTools.h"
#include "CadPerspectivePoints.h"
#include "Core/Entity/LineEntity.hpp"
#include "Core/Entity/PointEntity.hpp"
#include <optional>

// Draw model entities through a perspective layout viewport while the editor stays in paper space.
class CadPerspectiveDrawTool : public MiniCAD::ITool
{
public:
    CadPerspectiveDrawTool(uint64_t layout, bool point) : m_layout(layout), m_drawPoint(point) {}

    bool HandlesNavigation() const override { return true; }
    bool HasAnchor() const override { return m_start.has_value(); }
    MiniCAD::Math::Point3 GetAnchor() const override { return m_start.value_or(MiniCAD::Math::Point3{}); }
    std::string GetPrompt() const override
    {
        if (!m_issue.empty()) return m_issue;
        if (!m_viewport) return "单击要绘图的透视视口内部";
        if (m_drawPoint) return "在透视视口内指定模型点，Esc 退出";
        if (!m_start) return "在透视视口内指定模型直线起点";
        return "指定直线下一点，Enter／空格完成，Esc 退出";
    }

    bool OnInput(const MiniCAD::EditorContext& ctx) override
    {
        using namespace MiniCAD;
        m_overlay = &ctx.overlay;
        const auto& event = ctx.event;
        if (!ctx.scene.CurrentSpaceIsPaper() || ctx.scene.CurrentLayoutKey() != m_layout ||
            event.IsCancel() || event.IsRightClick() || event.IsKeyPressed(KeyCode::Enter) || event.IsKeyPressed(KeyCode::Space))
        { Cancel(); return true; }
        if (!event.IsLeftClick() && (event.Type != InputEventType::MouseMove || event.IsMiddleDrag()))
            return false;
        auto& camera = ctx.viewport.GetCamera();
        const auto paper = camera.ScreenToWorld(event.MouseX, event.MouseY);
        if (!m_viewport)
        {
            if (event.IsLeftClick() && !event.HasSnap)
            {
                const auto* picked = FindPaperViewport(ctx.scene, m_layout, paper, true);
                if (picked && picked->ClipKnown() && PerspectiveViewport(*picked).Valid()) { m_viewport = picked->GetID(); m_issue.clear(); }
                else m_issue = "请单击有效的透视视口内部，Esc 退出";
            }
            return true;
        }

        const auto* view = dynamic_cast<const ViewportEntity*>(ctx.scene.GetEntity(m_viewport));
        if (!view || !view->IsPerspective()) { Cancel(); return true; }
        ctx.overlay.Clear();
        const PerspectiveViewport projection(*view);
        if (!projection.Valid()) { m_issue = "透视参数无效，Esc 退出后检查视口"; return true; }
        if (!event.HasSnap && !view->ContainsPaper(paper))
        { m_issue = "请在视口可见边界内指定点"; return true; }
        const auto point = m_points.Resolve(ctx, *view, projection, paper, ctx.scene.GetWorkingPlane());
        if (!point)
        { m_issue = "此处没有可见的工作平面交点，请捕捉模型特征点或调整工作平面"; return true; }
        const auto& layer = ctx.scene.GetLayerManager().GetActiveLayer();
        if (layer.IsLocked() || !layer.IsVisible() || view->IsLayerFrozen(ctx.scene.GetLayerManager().GetActiveLayerID()))
        { m_issue = "当前图层在此视口不可绘图，请切换图层"; return true; }

        m_issue.clear();
        if (event.IsLeftClick())
        {
            if (m_drawPoint)
                Commit(ctx, std::make_unique<PointEntity>(ctx.scene.NextObjectID(), point->model));
            else
            {
                if (m_start && (point->model - *m_start).Length() > 1e-9)
                    Commit(ctx, std::make_unique<LineEntity>(ctx.scene.NextObjectID(), *m_start, point->model));
                m_start = point->model;
            }
        }
        if (m_start && !event.IsLeftClick())
            DrawPreview(ctx, *view, projection, *m_start, point->model);
        if (point->snapped || m_drawPoint)
            ctx.overlay.AddPoint(point->paper, {0.35f, 0.85f, 1, 1});
        return true;
    }

    void Cancel() override { if (m_overlay) m_overlay->Clear(); if (OnFinished) OnFinished(); }
    void OnSceneChanged() override { Cancel(); }

private:
    using Point = MiniCAD::Math::Point3;

    static void DrawPreview(const MiniCAD::EditorContext& ctx, const MiniCAD::ViewportEntity& view,
                            const MiniCAD::PerspectiveViewport& projection, const Point& start, const Point& end)
    {
        ForEachPerspectiveSegment(view, projection, PerspectivePaperBoundary(view), start, end,
            [&](const Point& a, const Point& b) { ctx.overlay.AddLine(a, b, {0.35f, 0.85f, 1, 1}); });
    }

    static void Commit(const MiniCAD::EditorContext& ctx, std::unique_ptr<MiniCAD::Entity> entity)
    {
        ctx.ApplyCurrentAttr(*entity);
        entity->GetAttr().InPaperSpace = false;
        entity->GetAttr().LayoutKey = 0;
        ctx.cmdStack.Execute(std::make_unique<MiniCAD::AddEntityCommand>(std::move(entity)), ctx.scene);
    }

    uint64_t m_layout, m_viewport = 0;
    CadPerspectivePoints m_points;
    bool m_drawPoint;
    MiniCAD::Overlay* m_overlay = nullptr;
    std::optional<Point> m_start;
    std::string m_issue;
};
