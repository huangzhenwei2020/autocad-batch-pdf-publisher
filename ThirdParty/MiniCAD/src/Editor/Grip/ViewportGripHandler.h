#pragma once
#include "IEntityGripHandler.h"
#include "GripType.h"
#include "Core/Entity/ViewportEntity.hpp"
#include <cmath>

namespace MiniCAD
{
    class ViewportGripHandler : public IEntityGripHandler
    {
        using Snapshot = DragEntityEntry::ViewportSnapshot;
        struct State : IGripDragState { Snapshot Before; };
        static Snapshot Capture(const ViewportEntity& viewport)
        { return {viewport.GetCenter(), viewport.GetWidth(), viewport.GetHeight(), viewport.GetViewHeight()}; }
        static void Restore(ViewportEntity& viewport, const Snapshot& state)
        { viewport.SetCenter(state.Center); viewport.SetSize(state.Width, state.Height); viewport.SetViewHeight(state.ViewHeight); }
    public:
        void BuildGrips(Entity* entity, std::vector<Grip>& grips) override
        {
            const auto& viewport = static_cast<const ViewportEntity&>(*entity);
            if (viewport.GetViewportId() == 1 && !viewport.ShowsModel()) return;
            const auto center = viewport.GetCenter();
            const double x = viewport.GetWidth() / 2, y = viewport.GetHeight() / 2;
            const Math::Point3 points[] = {{center.x - x, center.y - y, 0}, {center.x + x, center.y - y, 0},
                                         {center.x + x, center.y + y, 0}, {center.x - x, center.y + y, 0}, center};
            for (int index = 0; index < 5; ++index) grips.push_back({entity->GetID(), Grip::Type::Corner, points[index], index});
        }
        std::unique_ptr<IGripDragState> BeginDrag(Entity* entity, const Grip&) override
        {
            auto state = std::make_unique<State>(); state->Before = Capture(static_cast<const ViewportEntity&>(*entity)); return state;
        }
        void UpdateDrag(Entity* entity, IGripDragState* state, const Grip& active, const Math::Point3& point, std::vector<Grip>& grips) override
        {
            auto& viewport = static_cast<ViewportEntity&>(*entity);
            const auto& before = static_cast<State*>(state)->Before;
            Restore(viewport, before);
            if (active.SubIndex == 4) viewport.SetCenter(point);
            else
            {
                const double sx[] = {-1, 1, 1, -1}, sy[] = {-1, -1, 1, 1};
                const int opposite = (active.SubIndex + 2) % 4;
                const double ax = before.Center.x + sx[opposite] * before.Width / 2;
                const double ay = before.Center.y + sy[opposite] * before.Height / 2;
                const double width = std::max(1e-6, std::abs(point.x - ax)), height = std::max(1e-6, std::abs(point.y - ay));
                viewport.SetCenter({(point.x + ax) / 2, (point.y + ay) / 2, before.Center.z});
                viewport.SetSize(width, height);
                viewport.SetViewHeight(before.ViewHeight * height / before.Height);
            }
            std::vector<Grip> updated; BuildGrips(entity, updated);
            for (auto& grip : grips) if (grip.OwnerID == entity->GetID()) grip.WorldPos = updated[grip.SubIndex].WorldPos;
        }
        bool EndDrag(Entity* entity, IGripDragState* state, DragEntityEntry& entry) override
        {
            const auto& before = static_cast<State*>(state)->Before;
            const auto after = Capture(static_cast<const ViewportEntity&>(*entity));
            if (std::abs(before.Center.x - after.Center.x) < 1e-8 && std::abs(before.Center.y - after.Center.y) < 1e-8 &&
                std::abs(before.Width - after.Width) < 1e-8 && std::abs(before.Height - after.Height) < 1e-8) return false;
            entry.Id = entity->GetID(); entry.Kind = DragEntityEntry::Kind::Viewport;
            entry.BeforeViewport = before; entry.AfterViewport = after; return true;
        }
        void CancelDrag(Entity* entity, IGripDragState* state) override
        { Restore(static_cast<ViewportEntity&>(*entity), static_cast<State*>(state)->Before); }
    };
}
