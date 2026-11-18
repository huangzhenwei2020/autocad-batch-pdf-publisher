#pragma once
#include "Scene/Scene.h"
#include "Editor/Tools/ITool.h"
#include "Editor/EditorContext.h"
#include "Document/Command/AddEntityCommand.h"
#include "Core/Math/Point3.hpp"
#include "Core/Entity/CircleEntity.hpp"
#include "Core/Log.h"
#include <cmath>

namespace MiniCAD
{
    class CircleTool : public ITool
    {
    public:
        bool SupportsWorkingPlane() const override { return true; }
        CircleTool()
        {
            LOG_DEBUG("[CircleTool] click center | click radius | right-click/ESC exits");
        }

        ~CircleTool()
        {
            LOG_DEBUG("Exit drawing");
        }

        bool OnInput(const EditorContext& ctx) override
        {
            m_ctx = &ctx;
            m_overlay = &ctx.overlay;      // 事件之外只用它：m_ctx 指向的栈对象事件返回后即失效
            const auto& e = ctx.event;

            if (e.IsLeftClick())
            {
                auto pt = GetPoint(e);
                if (!m_hasCenter)
                {
                    m_center = pt;
                    m_hasCenter = true;
                }
                else
                {
                    double r = Distance2D(m_center, pt);
                    if (r > Math::LengthEPS)
                    {
                        Commit(m_center, r);
                    }
                    Reset();
                    if (r > Math::LengthEPS && OnFinished) OnFinished();
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

            if (e.Type == InputEventType::MouseMove && m_hasCenter)
            {
                auto  cursor = GetPoint(e);
                double r = Distance2D(m_center, cursor);
                m_ctx->overlay.Clear();

                const  auto& layer = m_ctx->scene.GetLayerManager().GetActiveLayer();

                m_ctx->overlay.AddCircle(Circle{m_center, r, m_ctx->scene.GetWorkingPlane().Normal()}, layer.GetColor());
                m_ctx->overlay.AddLine  (m_center, cursor, { 0.6, 0.6, 0.6, 0.4 });

                return false;
            }

            return false;
        }

        bool HasAnchor() const override
        {
            return m_hasCenter;
        }

        Math::Point3 GetAnchor() const override
        {
            return m_center;
        }

        std::string GetPrompt() const override
        {
            return m_hasCenter ? Tr("指定半径 [右键/ESC 退出]:")
                               : Tr("指定圆心:");
        }

        void OnSceneChanged() override
        {
            Reset();
        }

    private:
        Math::Point3 GetPoint(const InputEvent& e) const
        {
            const auto& plane = m_ctx->scene.GetWorkingPlane();
            auto point = e.SnapWorld;
            if (!e.HasSnap && !m_ctx->viewport.GetCamera().ScreenToPlane(e.MouseX, e.MouseY, plane, point))
                point = plane.Origin;
            auto local = plane.ToLocal(point);
            local.z = m_hasCenter ? plane.ToLocal(m_center).z : local.z;
            return plane.ToWorld(local);
        }

        static double Distance2D(const Math::Point3& a, const Math::Point3& b)
        {
            double dx = b.x - a.x;
            double dy = b.y - a.y;
            return std::hypot(dx, dy, b.z - a.z);
        }

        void Commit(const Math::Point3& center, double radius)
        {
            auto id = m_ctx->scene.NextObjectID();
            auto circle = std::make_unique<CircleEntity>(id, center, radius);
            m_ctx->ApplyCurrentAttr(*circle);
            circle->SetExtrusion(m_ctx->scene.GetWorkingPlane().Normal());
            auto cmd = std::make_unique<AddEntityCommand>(std::move(circle));
            m_ctx->cmdStack.Execute(std::move(cmd), m_ctx->scene);
            LOG_DEBUG("Circle Id %d center(%.3f,%.3f) r=%.3f",
                static_cast<int>(id), center.x, center.y, radius);
        }

        void Reset()
        {
            m_hasCenter = false;
            m_center = {};
            if (m_overlay) m_overlay->Clear();
        }

    private:
        const EditorContext* m_ctx = nullptr;
        Overlay*              m_overlay = nullptr;

        bool         m_hasCenter = false;
        Math::Point3 m_center{};
    };
}
