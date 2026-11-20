#pragma once
#include "Document/Command/LayoutCommands.h"
#include "Document/Command/GeometryEditCommand.h"
#include "Document/Command/AddEntityCommand.h"
#include "Document/PerspectiveViewport.hpp"
#include "Editor/Tools/ITool.h"
#include "Editor/EditorContext.h"
#include "Core/Entity/ViewportEntity.hpp"
#include <cmath>
#include <stdexcept>
#include <limits>

inline const MiniCAD::ViewportEntity* FindPaperViewport(const MiniCAD::Scene& scene, uint64_t layout,
    const MiniCAD::Math::Point3& point, bool perspectiveOnly = false)
{
    using namespace MiniCAD;
    const ViewportEntity* picked = nullptr;
    double area = std::numeric_limits<double>::max();
    scene.ForEachObject([&](const Object& object)
    {
        const auto* viewport = dynamic_cast<const ViewportEntity*>(&object);
        if (!viewport || !viewport->GetAttr().InPaperSpace || viewport->GetAttr().LayoutKey != layout ||
            !viewport->ShowsModel() || (perspectiveOnly && !viewport->IsPerspective()) ||
            !viewport->ContainsPaper(point) || !viewport->GetAttr().Visible) return;
        const auto* layer = scene.GetLayerManager().GetLayer(viewport->GetLayerID());
        if (layer && !layer->IsVisible()) return;
        const double candidate = viewport->GetWidth() * viewport->GetHeight();
        if (candidate < area) { area = candidate; picked = viewport; }
    });
    return picked;
}

struct CadViewportParameters
{
    double X, Y, Width, Height, ModelX, ModelY, Ratio, Twist;
    int Locked, Fit;
};

inline void ApplyViewportParameters(MiniCAD::ViewportEntity& viewport, const CadViewportParameters& parameters,
                                    const MiniCAD::Scene& scene)
{
    viewport.SetCenter({parameters.X, parameters.Y, 0});
    viewport.SetSize(parameters.Width, parameters.Height);
    viewport.SetViewCenter({parameters.ModelX, parameters.ModelY, viewport.GetViewCenter().z});
    viewport.SetViewHeight(parameters.Height * parameters.Ratio);
    viewport.SetTwist(parameters.Twist * std::acos(-1.0) / 180.0);
    viewport.SetLocked(parameters.Locked != 0);
    if (!parameters.Fit) return;
    MiniCAD::AABB bounds;
    bool any = false;
    scene.ForEachObject([&](const MiniCAD::Object& object)
    {
        const auto* entity = dynamic_cast<const MiniCAD::Entity*>(&object);
        if (!entity || entity->GetAttr().InPaperSpace || !entity->GetAttr().Visible || entity->IsBoundless()) return;
        const auto* layer = scene.GetLayerManager().GetLayer(entity->GetLayerID());
        if (layer && !layer->IsVisible()) return;
        const auto box = entity->GetBoundingBox();
        if (!any) { bounds = box; any = true; }
        else bounds.Expand(box);
    });
    if (!any) return;
    const auto center = bounds.Center();
    const double width = bounds.Max.x - bounds.Min.x, height = bounds.Max.y - bounds.Min.y;
    const double angle = viewport.GetTwist();
    const double viewWidth = width * std::abs(std::cos(angle)) + height * std::abs(std::sin(angle));
    const double viewHeight = width * std::abs(std::sin(angle)) + height * std::abs(std::cos(angle));
    viewport.SetViewCenter(center);
    viewport.SetViewHeight(std::max({1.0, viewHeight, viewWidth * parameters.Height / parameters.Width}) * 1.1);
}

inline void ApplyViewportClipFrame(MiniCAD::ViewportEntity& viewport, double x, double y, double width, double height)
{
    const auto center = viewport.GetCenter();
    const double ratio = viewport.GetViewHeight() / viewport.GetHeight();
    viewport.SetViewCenter(viewport.GetViewCenter() + viewport.PaperVectorToModel(x - center.x, y - center.y));
    viewport.SetCenter({x, y, center.z});
    viewport.SetSize(width, height);
    viewport.SetViewHeight(height * ratio);
}

inline void ApplyViewportClip(MiniCAD::ViewportEntity& viewport, const std::vector<MiniCAD::Math::Point3>& points)
{
    if (points.size() < 3) throw std::invalid_argument("请至少指定三个不同的顶点。");
    double minX=points[0].x,maxX=minX,minY=points[0].y,maxY=minY;
    for (const auto& p : points) { if (!std::isfinite(p.x) || !std::isfinite(p.y)) throw std::invalid_argument("裁剪顶点需要有效坐标。");
        minX=std::min(minX,p.x); maxX=std::max(maxX,p.x); minY=std::min(minY,p.y); maxY=std::max(maxY,p.y); }
    const double width=maxX-minX,height=maxY-minY;
    if (width <= 1e-9 || height <= 1e-9) throw std::invalid_argument("裁剪边界需要形成有效面积。");
    std::vector<MiniCAD::Math::Point3> normalized;
    const double x=(minX+maxX)/2,y=(minY+maxY)/2;
    for (const auto& p : points) normalized.push_back({(p.x-x)/width,(p.y-y)/height,0});
    auto cross=[](const auto& a,const auto& b,const auto& c) { return (b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x); };
    auto on=[](const auto& a,const auto& b,const auto& p) { return p.x>=std::min(a.x,b.x)-1e-10 && p.x<=std::max(a.x,b.x)+1e-10 && p.y>=std::min(a.y,b.y)-1e-10 && p.y<=std::max(a.y,b.y)+1e-10; };
    double area=0;
    for (size_t i=0;i<normalized.size();++i)
    {
        const auto& a=normalized[i]; const auto& b=normalized[(i+1)%normalized.size()]; area+=a.x*b.y-b.x*a.y;
        if (std::hypot(a.x-b.x,a.y-b.y)<1e-10) throw std::invalid_argument("相邻裁剪顶点不能重合。");
        for (size_t j=i+1;j<normalized.size();++j)
        {
            if (j==i+1 || (i==0 && j+1==normalized.size())) continue;
            const auto& c=normalized[j]; const auto& d=normalized[(j+1)%normalized.size()];
            const double abC=cross(a,b,c),abD=cross(a,b,d),cdA=cross(c,d,a),cdB=cross(c,d,b);
            if ((abC*abD<0 && cdA*cdB<0) || (std::abs(abC)<1e-10 && on(a,b,c)) || (std::abs(abD)<1e-10 && on(a,b,d)) ||
                (std::abs(cdA)<1e-10 && on(c,d,a)) || (std::abs(cdB)<1e-10 && on(c,d,b)))
                throw std::invalid_argument("裁剪边界不能自相交，请退回顶点后重画。");
        }
    }
    if (std::abs(area)<1e-10) throw std::invalid_argument("裁剪边界需要形成有效面积。");
    ApplyViewportClipFrame(viewport,x,y,width,height);
    viewport.SetClipCoordinates(std::move(normalized));
}

class CadViewportClipTool : public MiniCAD::ITool
{
public:
    CadViewportClipTool(uint64_t id, uint64_t layout, int kind = 0) : m_id(id), m_layout(layout), m_kind(kind) {}
    bool HasAnchor() const override { return m_kind > 1 && !m_points.empty(); }
    MiniCAD::Math::Point3 GetAnchor() const override { return m_points.empty() ? MiniCAD::Math::Point3{} : m_points.front(); }
    std::string GetPrompt() const override
    {
        if (!m_error.empty()) return m_error;
        if (m_kind == 2) return m_points.empty() ? "指定圆形视口的圆心" : "指定圆周点，单击完成，Backspace退回，Esc取消";
        if (m_kind == 3) return m_points.empty() ? "指定椭圆外包矩形第一个角点" : "指定对角点，单击完成，Backspace退回，Esc取消";
        if (m_kind == 4)
        {
            if (m_points.empty()) return "指定旋转椭圆的中心";
            if (m_points.size() == 1) return "指定长轴端点，Backspace退回，Esc取消";
            return "指定短轴半径，单击完成，Backspace退回，Esc取消";
        }
        return "指定裁剪顶点，Enter／空格完成，Backspace退回顶点，Esc取消";
    }
    bool OnInput(const MiniCAD::EditorContext& context) override
    {
        using namespace MiniCAD;
        m_overlay = &context.overlay;
        auto* viewport = dynamic_cast<ViewportEntity*>(context.scene.GetEntity(m_id));
        if (!viewport || context.scene.CurrentLayoutKey() != m_layout || context.event.IsCancel()) { Cancel(); return true; }
        if (context.event.IsKeyPressed(KeyCode::Backspace))
        {
            if (!m_points.empty()) m_points.pop_back();
            m_error.clear(); context.overlay.Clear();
            return true;
        }
        const auto point = context.event.HasSnap ? context.event.SnapWorld : context.viewport.GetCamera().ScreenToWorld(context.event.MouseX,context.event.MouseY);
        if (context.event.IsLeftClick()) { m_points.push_back(point); m_error.clear(); }
        const bool finish = context.event.IsKeyPressed(KeyCode::Enter) || context.event.IsKeyPressed(KeyCode::Space) ||
            context.event.IsRightClick() || (m_kind > 1 && m_points.size() == CurvePointCount() && context.event.IsLeftClick());
        if (finish)
        {
            try
            {
                if (context.scene.IsEntityLocked(*viewport)) throw std::invalid_argument("视口所在图层已锁定。");
                auto after = viewport->Clone(m_id);
                auto& edited = static_cast<ViewportEntity&>(*after);
                if (m_kind > 1)
                {
                    const auto [center, frame] = Curve(m_points);
                    ApplyViewportClipFrame(edited, center.x, center.y, frame.Width, frame.Height);
                    edited.SetClipEllipse(frame.Correlation);
                }
                else ApplyViewportClip(edited, m_points);
                std::vector<GeometryEditCommand::Item> edits;
                edits.push_back({m_id, viewport->Clone(m_id), std::move(after)});
                context.cmdStack.Execute(std::make_unique<GeometryEditCommand>("裁剪布局视口",std::move(edits)),context.scene);
                Cancel();
            }
            catch (const std::exception& error)
            {
                m_error = error.what();
                if (m_kind > 1 && m_points.size() == CurvePointCount()) m_points.pop_back();
            }
            return true;
        }
        if (context.event.Type == InputEventType::MouseMove || context.event.IsLeftClick())
        {
            context.overlay.Clear();
            auto preview = m_points;
            if (!preview.empty()) preview.push_back(point);
            if (m_kind > 1)
            {
                try
                {
                    const auto [center, frame] = Curve(preview);
                    ViewportEntity outline(0, center, frame.Width, frame.Height);
                    outline.SetClipEllipse(frame.Correlation);
                    preview = outline.PaperBoundary();
                }
                catch (const std::invalid_argument&) { preview.clear(); }
            }
            for (size_t i = 1; i < preview.size(); ++i) context.overlay.AddLine(preview[i-1],preview[i],{0.4f,0.75f,1,1});
            if (preview.size() > 2) context.overlay.AddLine(preview.back(),preview.front(),{0.4f,0.75f,1,1});
        }
        return false;
    }
    void Cancel() override { if (m_overlay) m_overlay->Clear(); if (OnFinished) OnFinished(); }
    void OnSceneChanged() override { Cancel(); }
private:
    size_t CurvePointCount() const { return m_kind == 4 ? 3 : 2; }
    std::pair<MiniCAD::Math::Point3, MiniCAD::ViewportEntity::EllipseClipFrame>
    Curve(const std::vector<MiniCAD::Math::Point3>& points) const
    {
        using namespace MiniCAD;
        if (points.size() < CurvePointCount())
            throw std::invalid_argument(m_kind == 4 ? "请依次指定椭圆中心、长轴端点和短轴半径。" : "请指定两个不同的点。");
        const auto delta = points[1] - points[0];
        Math::Point3 center = points[0];
        ViewportEntity::EllipseClipFrame frame{};
        if (m_kind == 4)
        {
            const double major = std::hypot(delta.x, delta.y);
            if (!(major > 1e-9)) throw std::invalid_argument("长轴半径需要大于0。");
            const Math::Vec3 perpendicular{-delta.y / major, delta.x / major, 0};
            const double minor = std::abs(Math::Dot(points[2] - center, perpendicular));
            if (!(minor > 1e-9 && minor <= major)) throw std::invalid_argument("短轴半径需要大于0且不超过长轴半径。");
            frame = ViewportEntity::EllipseClipFrame::FromAxes({delta.x, delta.y, 0}, perpendicular * minor);
        }
        else if (m_kind == 2)
        {
            const double diameter = 2 * std::hypot(delta.x, delta.y);
            frame = {diameter, diameter, 0};
        }
        else
        {
            center = center + delta * .5;
            frame = {std::abs(delta.x), std::abs(delta.y), 0};
        }
        if (!(frame.Width > 1e-9 && frame.Height > 1e-9 && std::isfinite(frame.Width) &&
            std::isfinite(frame.Height) && std::abs(frame.Correlation) < 1))
            throw std::invalid_argument("请指定有效的圆或椭圆半径。");
        center.z = 0;
        return {center, frame};
    }
    uint64_t m_id, m_layout;
    int m_kind;
    std::vector<MiniCAD::Math::Point3> m_points;
    MiniCAD::Overlay* m_overlay = nullptr;
    std::string m_error;
};

class CadViewportTool : public MiniCAD::ITool
{
public:
    CadViewportTool(CadViewportParameters parameters, uint64_t layout) : m_parameters(parameters), m_layout(layout) {}
    bool OnInput(const MiniCAD::EditorContext& context) override
    {
        m_overlay = &context.overlay;
        if (context.event.IsCancel() || context.event.IsRightClick() || context.scene.CurrentLayoutKey() != m_layout)
        { Cancel(); return true; }
        const auto point = context.event.HasSnap ? context.event.SnapWorld :
            context.viewport.GetCamera().ScreenToWorld(context.event.MouseX, context.event.MouseY);
        if (context.event.IsLeftClick())
        {
            if (!m_hasCorner) { m_corner = point; m_hasCorner = true; return true; }
            const double width = std::abs(point.x - m_corner.x), height = std::abs(point.y - m_corner.y);
            if (width < 1e-6 || height < 1e-6) return true;
            auto parameters = m_parameters;
            parameters.X = (point.x + m_corner.x) * 0.5; parameters.Y = (point.y + m_corner.y) * 0.5;
            parameters.Width = width; parameters.Height = height;
            int number = 2;
            context.scene.ForEachObject([&](const MiniCAD::Object& object)
            {
                if (const auto* viewport = dynamic_cast<const MiniCAD::ViewportEntity*>(&object);
                    viewport && viewport->GetAttr().LayoutKey == m_layout) number = std::max(number, viewport->GetViewportId() + 1);
            });
            auto viewport = std::make_unique<MiniCAD::ViewportEntity>(context.scene.NextObjectID(), MiniCAD::Math::Point3{}, width, height);
            ApplyViewportParameters(*viewport, parameters, context.scene);
            viewport->SetViewportId(number);
            context.ApplyCurrentAttr(*viewport);
            context.cmdStack.Execute(std::make_unique<MiniCAD::AddEntityCommand>(std::move(viewport)), context.scene);
            Cancel(); return true;
        }
        if (context.event.Type == MiniCAD::InputEventType::MouseMove && m_hasCorner)
        { context.overlay.Clear(); context.overlay.AddRect(m_corner, point, {0.4f, 0.75f, 1, 1}); }
        return false;
    }
    bool HasAnchor() const override { return m_hasCorner; }
    MiniCAD::Math::Point3 GetAnchor() const override { return m_corner; }
    std::string GetPrompt() const override { return m_hasCorner ? "指定视口另一个角点，单击完成" : "指定视口第一个角点"; }
    void Cancel() override { if (m_overlay) m_overlay->Clear(); m_hasCorner = false; if (OnFinished) OnFinished(); }
    void OnSceneChanged() override { Cancel(); }
private:
    CadViewportParameters m_parameters;
    uint64_t m_layout;
    bool m_hasCorner = false;
    MiniCAD::Math::Point3 m_corner;
    MiniCAD::Overlay* m_overlay = nullptr;
};

// Content navigation leaves the paper camera and viewport frame fixed.
class CadViewportNavigationTool : public MiniCAD::ITool
{
public:
    explicit CadViewportNavigationTool(uint64_t layout) : m_layout(layout) {}
    bool HandlesNavigation() const override { return true; }
    bool HidesCrosshair() const override { return m_before != nullptr; }
    std::string GetPrompt() const override
    {
        if (!m_id) return "单击要调整的视口内部";
        if (m_unsupported) return "此视口的观察参数暂不支持调整内容，请单击切换视口；Esc 退出";
        if (m_layerLocked) return "视口所在图层已锁定，请先在图层面板解锁；Esc 退出";
        return m_locked ? "视口已锁定，Esc 退出后选中边框，在属性面板中解锁" :
            "中键拖动平移内容，滚轮缩放；单击切换视口，Enter／空格／Esc 退出，Ctrl+Z 撤销";
    }
    bool OnInput(const MiniCAD::EditorContext& context) override
    {
        using namespace MiniCAD;
        m_scene = &context.scene;
        m_overlay = &context.overlay;
        const auto& e = context.event;
        if (context.scene.CurrentLayoutKey() != m_layout || e.IsCancel() || e.IsRightClick() ||
            e.IsKeyPressed(KeyCode::Enter) || e.IsKeyPressed(KeyCode::Space))
        { Cancel(); return true; }
        if (e.IsUndo() || e.IsRedo()) { Cancel(); return false; }
        if (e.Type == InputEventType::KeyDown || e.Type == InputEventType::KeyUp) return false;
        const auto point = context.viewport.GetCamera().ScreenToWorld(e.MouseX, e.MouseY);
        if (e.IsLeftClick() && !m_before)
        {
            const auto* picked = FindPaperViewport(context.scene, m_layout, point);
            m_id = picked ? picked->GetID() : 0;
        }
        auto* viewport = dynamic_cast<ViewportEntity*>(context.scene.GetEntity(m_id));
        if (!viewport) return true;
        m_layerLocked = context.scene.IsEntityLocked(*viewport);
        m_locked = viewport->IsLocked() || m_layerLocked;
        m_unsupported = viewport->IsPerspective() ? !PerspectiveViewport(*viewport).Valid() :
            (!viewport->IsPlanView() && !viewport->HasSpatialView());
        context.overlay.Clear();
        const auto boundary = viewport->PaperBoundary();
        for (size_t i=0;i<boundary.size();++i) context.overlay.AddLine(boundary[i],boundary[(i+1)%boundary.size()],{0.3f,0.8f,1,1});
        if (m_locked || m_unsupported) return true;
        if (e.Type == InputEventType::MouseButtonDown && e.Button == MouseButton::Middle)
        {
            m_before = viewport->Clone(m_id); m_anchor = point;
        }
        if (e.IsMiddleDrag() && m_before)
        {
            const auto* before = static_cast<const ViewportEntity*>(m_before.get());
            const auto delta = ViewOffset(*before, point.x - m_anchor.x, point.y - m_anchor.y);
            const auto center = before->GetViewCenter();
            viewport->SetViewCenter(center - delta);
            context.scene.MarkDirty();
        }
        if (e.Type == InputEventType::MouseButtonUp && e.Button == MouseButton::Middle && m_before)
            Commit(context, *viewport);
        if (e.IsZoom() && !m_before && viewport->ContainsPaper(point))
        {
            const double factor = std::pow(1.2, -std::clamp(double(e.WheelDelta), -10.0, 10.0));
            const double height = viewport->GetViewHeight() * factor;
            if (!std::isfinite(height) || height < 1e-9 || height > 1e15) return true;
            auto optics = viewport->GetCameraOptics();
            if (viewport->IsPerspective())
            {
                optics.Lens = PerspectiveViewport(*viewport).Lens() / factor;
                if (!std::isfinite(optics.Lens) || optics.Lens <= 0) return true;
            }
            m_before = viewport->Clone(m_id);
            const auto offset = ViewOffset(*viewport, point.x - viewport->GetCenter().x, point.y - viewport->GetCenter().y);
            const auto center = viewport->GetViewCenter();
            viewport->SetViewCenter(center + offset * (1 - factor));
            viewport->SetViewHeight(height);
            if (viewport->IsPerspective()) viewport->SetCameraOptics(optics);
            Commit(context, *viewport);
        }
        return true;
    }
    void Cancel() override
    {
        if (m_before && m_scene)
        { m_scene->RemoveEntity(m_id); m_scene->AddEntity(std::move(m_before)); m_scene->MarkDirty(); }
        if (m_overlay) m_overlay->Clear();
        if (OnFinished) OnFinished();
    }
    void OnSceneChanged() override { Cancel(); }
private:
    static MiniCAD::Math::Vec3 ViewOffset(const MiniCAD::ViewportEntity& viewport, double paperX, double paperY)
    {
        if (viewport.IsPerspective())
            return MiniCAD::PerspectiveViewport(viewport).TargetPlaneOffset(paperX, paperY);
        return viewport.PaperVectorToModel(paperX, paperY);
    }

    void Commit(const MiniCAD::EditorContext& context, MiniCAD::ViewportEntity& viewport)
    {
        const auto* before = static_cast<const MiniCAD::ViewportEntity*>(m_before.get());
        const auto a = before->GetViewCenter(), b = viewport.GetViewCenter();
        if (a.x == b.x && a.y == b.y && a.z == b.z && before->GetViewHeight() == viewport.GetViewHeight())
        { m_before.reset(); return; }
        auto after = viewport.Clone(m_id);
        context.scene.RemoveEntity(m_id); context.scene.AddEntity(m_before->Clone(m_id));
        std::vector<MiniCAD::GeometryEditCommand::Item> edits;
        edits.push_back({m_id, std::move(m_before), std::move(after)});
        context.cmdStack.Execute(std::make_unique<MiniCAD::GeometryEditCommand>("调整视口内容", std::move(edits)), context.scene);
    }
    uint64_t m_layout, m_id = 0;
    bool m_locked = false;
    bool m_layerLocked = false;
    bool m_unsupported = false;
    MiniCAD::Math::Point3 m_anchor;
    MiniCAD::Scene* m_scene = nullptr;
    MiniCAD::Overlay* m_overlay = nullptr;
    std::unique_ptr<MiniCAD::Entity> m_before;
};

class CadLayoutEditCommand : public MiniCAD::ICommand
{
public:
    CadLayoutEditCommand(const MiniCAD::Scene& scene, std::vector<MiniCAD::DrawingLayout> layouts,
                         std::vector<std::unique_ptr<MiniCAD::Entity>> copies = {})
        : m_before(scene.Layouts()), m_after(std::move(layouts)), m_copies(std::move(copies)) {}
    bool Execute(MiniCAD::Scene& scene) override
    {
        scene.SetLayouts(m_after);
        for (const auto& entity : m_copies) scene.AddEntity(entity->Clone(entity->GetID()));
        scene.ResolveAllInserts(); scene.MarkDirty(); return true;
    }
    void Undo(MiniCAD::Scene& scene) override
    {
        for (const auto& entity : m_copies) scene.RemoveEntity(entity->GetID());
        scene.SetLayouts(m_before); scene.MarkDirty();
    }
    std::string GetName() const override { return "修改图纸布局"; }
private:
    std::vector<MiniCAD::DrawingLayout> m_before, m_after;
    std::vector<std::unique_ptr<MiniCAD::Entity>> m_copies;
};
