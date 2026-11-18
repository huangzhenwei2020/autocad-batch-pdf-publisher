#pragma once
#include "Scene/Scene.h"
#include "Editor/Tools/ITool.h"
#include "Editor/EditorContext.h"
#include "Document/Command/AddEntityCommand.h"
#include "Core/Math/Point3.hpp"
#include "Core/Entity/EllipseEntity.hpp"
#include "Core/Log.h"
#include <cmath>

namespace MiniCAD
{
    // 椭圆 / 椭圆弧
    //   椭圆  ：圆心 → 长轴端点 → 短轴端点
    //   椭圆弧：同上定出椭圆后 → 起始角方向点 → 终止角方向点（逆时针，同 AutoCAD ELLIPSE 的「圆弧」选项）
    //           方向点经 Ellipse::ParamOfPoint 换算为参数角：椭圆弧端点落在「圆心→方向点」射线上
    class EllipseTool : public ITool
    {
    public:
        explicit EllipseTool(bool arcMode = false)
            : m_arcMode(arcMode)
        {
            LOG_DEBUG("[EllipseTool] click center | click major axis end | click minor axis end%s | right-click/ESC exits",
                      arcMode ? " | click start angle | click end angle" : "");
        }

        ~EllipseTool()
        {
            LOG_DEBUG("Exit drawing");
        }

        bool SupportsWorkingPlane() const override { return true; }
        bool DrawsAtAnchorElevation() const override { return HasAnchor(); }

        bool OnInput(const EditorContext& ctx) override
        {
            m_ctx = &ctx;
            m_overlay = &ctx.overlay;      // 事件之外只用它：m_ctx 指向的栈对象事件返回后即失效
            const auto& e = ctx.event;

            if (e.IsLeftClick())
            {
                auto pt = GetPoint(e);
                switch (m_step)
                {
                    case 0:
                        m_center = pt;
                        m_step   = 1;
                        LOG_INFO("[EllipseTool] center (%.3f, %.3f) set, click major axis end",
                               pt.x, pt.y);
                        break;

                    case 1:
                    {
                        const auto axis = AxisComponents(pt);
                        double rx = std::hypot(axis.x, axis.y);
                        if (rx < Math::LengthEPS)
                        {
                            LOG_WARN("[EllipseTool] major axis too short, click again");
                            break;
                        }
                        m_rx       = rx;
                        m_rotation = std::atan2(axis.y, axis.x);
                        m_step     = 2;
                        LOG_INFO("[EllipseTool] major axis set, rx=%.3f rot=%.1f°, click minor axis end",
                               m_rx, m_rotation * 180.0 / Math::PI);
                        break;
                    }

                    case 2:
                    {
                        double ry = ComputeRY(pt);
                        if (ry < Math::LengthEPS)
                        {
                            LOG_WARN("[EllipseTool] minor axis too short, click again");
                            break;
                        }
                        m_ry = ry;
                        if (!m_arcMode)
                        {
                            Commit(CurrentEllipse());
                            Reset();
                            break;
                        }
                        m_step = 3;
                        break;
                    }

                    case 3:
                        m_startParam = CurrentEllipse().ParamOfPoint(pt);
                        m_step       = 4;
                        break;

                    case 4:
                    {
                        Ellipse el = CurrentEllipse();
                        el.SetParams(m_startParam, el.ParamOfPoint(pt));
                        if (el.IsFull())
                        {
                            LOG_WARN("[EllipseTool] start and end angles coincide, click the end angle again");
                            break;
                        }
                        Commit(el);
                        Reset();
                        break;
                    }
                }
                return true;
            }

            if (e.IsRightClick() || e.IsCancel())
            {
                m_ctx->overlay.Clear();
                Reset();
                if (OnFinished) OnFinished();
                return true;
            }

            if (e.Type == InputEventType::MouseMove && m_step > 0)
            {
                auto cursor = GetPoint(e);
                m_ctx->overlay.Clear();

                const auto& layer = m_ctx->scene.GetLayerManager().GetActiveLayer();

                const Math::Color4 layerColor  = layer.GetColor();
                const Math::Color4 helperColor = { 0.6, 0.6, 0.6, 0.7 };
                const Math::Color4 axisColor   = { 0.4, 0.8, 1.0, 0.7 };

                if (m_step == 1)
                {
                    m_ctx->overlay.AddLine(m_center, cursor, helperColor);
                    const double radius = (cursor - m_center).Length();
                    if (radius > Math::LengthEPS)
                        m_ctx->overlay.AddCircle(Circle{m_center, radius, Plane().Normal()}, layerColor);
                }
                else if (m_step == 2)
                {
                    auto ellipse = CurrentEllipse();
                    ellipse.RadiusY = ComputeRY(cursor);
                    m_ctx->overlay.AddLine(ellipse.VertexW(), ellipse.VertexE(), axisColor);
                    m_ctx->overlay.AddLine(ellipse.VertexS(), ellipse.VertexN(), axisColor);
                    if (ellipse.IsValid()) m_ctx->overlay.AddEllipse(ellipse, layerColor);
                }
                else if (m_step == 3)
                {
                    // 整椭圆淡显 + 起始角方向线
                    m_ctx->overlay.AddEllipse(CurrentEllipse(), helperColor);
                    m_ctx->overlay.AddLine(m_center, cursor, axisColor);
                }
                else if (m_step == 4)
                {
                    Ellipse el = CurrentEllipse();
                    m_ctx->overlay.AddEllipse(el, helperColor);
                    m_ctx->overlay.AddLine(m_center, el.PointAt(m_startParam), axisColor);
                    m_ctx->overlay.AddLine(m_center, cursor, axisColor);
                    el.SetParams(m_startParam, el.ParamOfPoint(cursor));
                    if (!el.IsFull())
                        m_ctx->overlay.AddEllipse(el, layerColor);
                }

                return false;
            }

            return false;
        }

        bool HasAnchor() const override { return m_step > 0; }

        Math::Point3 GetAnchor() const override { return m_center; }

        std::string GetPrompt() const override
        {
            switch (m_step)
            {
                case 1:  return Tr("指定长轴端点:");
                case 2:  return m_arcMode ? Tr("指定短轴端点:") : Tr("指定短轴端点 [右键/ESC 退出]:");
                case 3:  return Tr("指定起始角度:");
                case 4:  return Tr("指定终止角度 [右键/ESC 退出]:");
                default: return Tr("指定圆心:");
            }
        }

        void OnSceneChanged() override { Reset(); }

    private:
        Math::WorkingPlane Plane() const
        {
            return m_ctx->scene.CurrentSpaceIsPaper() ? Math::WorkingPlane{} : m_ctx->scene.GetWorkingPlane();
        }

        Math::Point3 GetPoint(const InputEvent& event) const
        {
            auto plane = Plane();
            if (HasAnchor()) plane.Origin = m_center;
            if (event.HasSnap)
            {
                auto local = plane.ToLocal(event.SnapWorld);
                if (HasAnchor()) local.z = 0;
                return plane.ToWorld(local);
            }
            Math::Point3 point = plane.Origin;
            m_ctx->viewport.GetCamera().ScreenToPlane(event.MouseX, event.MouseY, plane, point);
            return point;
        }

        Math::Point3 AxisComponents(const Math::Point3& point) const
        {
            const auto delta = point - m_center;
            return Math::WcsToOcs(Math::BuildOCS(Plane().Normal()), {delta.x, delta.y, delta.z});
        }

        double ComputeRY(const Math::Point3& point) const
        {
            const auto [x, y] = CurrentEllipse().Axes();
            return std::abs(Math::Dot(point - m_center, y));
        }

        Ellipse CurrentEllipse() const
        {
            return {m_center, m_rx, m_ry, m_rotation, 0, Math::TwoPI, Plane().Normal()};
        }

        void Commit(const Ellipse& el)
        {
            auto id     = m_ctx->scene.NextObjectID();
            auto entity = std::make_unique<EllipseEntity>(id, el);
            m_ctx->ApplyCurrentAttr(*entity);
            auto cmd    = std::make_unique<AddEntityCommand>(std::move(entity));
            m_ctx->cmdStack.Execute(std::move(cmd), m_ctx->scene);

            LOG_DEBUG("Ellipse Id %d center(%.3f,%.3f) rx=%.3f ry=%.3f rot=%.1f° param[%.1f°, %.1f°]",
                   static_cast<int>(id),
                   el.Center.x, el.Center.y, el.RadiusX, el.RadiusY,
                   el.Rotation * 180.0 / Math::PI,
                   el.StartParam * 180.0 / Math::PI, el.EndParam * 180.0 / Math::PI);
        }

        void Reset()
        {
            m_step       = 0;
            m_rx         = 0.0;
            m_ry         = 0.0;
            m_rotation   = 0.0;
            m_startParam = 0.0;
            m_center   = {};
            if (m_overlay) m_overlay->Clear();
        }

    private:
        const EditorContext* m_ctx = nullptr;
        Overlay*              m_overlay = nullptr;

        bool         m_arcMode    = false;
        int          m_step       = 0;
        Math::Point3 m_center{};
        double       m_rx         = 0.0;
        double       m_ry         = 0.0;
        double       m_rotation   = 0.0;
        double       m_startParam = 0.0;     // 椭圆弧：起始参数角
    };
}
