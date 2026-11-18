#pragma once
#include "Document/Document.h"
#include "Document/Command/MoveCommand.h"
#include "Document/Command/EntityRotate.h"
#include "Document/Command/EntityMirror.h"
#include "Editor/Grip/CircleGripHandler.h"
#include "Editor/Grip/ArcGripHandler.h"
#include "Import/CadExchange.h"
#include <stdexcept>
#include <regex>

inline void CheckSpatialCurves()
{
    using namespace MiniCAD;
    auto check = [](bool value) { if (!value) throw std::runtime_error("Spatial circle/arc invariant failed"); };
    auto near = [](const Math::Point3& a, const Math::Point3& b) { return (a-b).Length() < 1e-7; };
    for (const Math::Vec3 normal : {Math::Vec3{0,0,1}, {0,0,-1}, {0,-1,0}, {1,2,3}, {.001,.002,1}})
    {
        Document document;
        auto& scene = document.GetScene();
        const Circle circle{{120,230,340}, 25, normal.Normalized()};
        const Arc arc{circle.Center, 25, 5.5, 7.2, circle.Normal};
        auto ce = std::make_unique<CircleEntity>(scene.NextObjectID(), circle.Center, circle.Radius);
        ce->SetCircle(circle);
        auto ae = std::make_unique<ArcEntity>(scene.NextObjectID(), arc.Center, arc.Radius, arc.StartAngle, arc.EndAngle);
        ae->SetArc(arc);
        for (int i=0; i<=360; ++i)
        {
            const auto point = circle.PointAt(Math::TwoPI*i/360);
            const auto box = circle.GetBounds();
            check(point.x >= box.Min.x-1e-8 && point.x <= box.Max.x+1e-8 &&
                point.y >= box.Min.y-1e-8 && point.y <= box.Max.y+1e-8 && point.z >= box.Min.z-1e-8 && point.z <= box.Max.z+1e-8);
            check(near(circle.ClosestPoint(point + circle.Normal*10),point));
            const auto ap = arc.PointAt(arc.StartAngle+arc.SweepAngle()*i/360);
            const auto ab = arc.GetBounds();
            check(ap.x >= ab.Min.x-1e-8 && ap.x <= ab.Max.x+1e-8 && ap.y >= ab.Min.y-1e-8 && ap.y <= ab.Max.y+1e-8 &&
                ap.z >= ab.Min.z-1e-8 && ap.z <= ab.Max.z+1e-8);
        }
        CircleGripHandler cg;
        ArcGripHandler ag;
        std::vector<Grip> grips;
        cg.BuildGrips(ce.get(),grips);
        check(grips.size()==5 && near(grips[2].WorldPos,circle.QuadrantN()));
        auto state=cg.BeginDrag(ce.get(),grips[1]);
        cg.UpdateDrag(ce.get(),state.get(),grips[1],circle.Center+(circle.QuadrantN()-circle.Center)*2,grips);
        check(std::abs(ce->GetCircle().Radius-50)<1e-8 && (ce->GetExtrusion()-circle.Normal).Length()<1e-8);
        DragEntityEntry circleEntry;
        check(cg.EndDrag(ce.get(),state.get(),circleEntry));
        cg.CancelDrag(ce.get(),state.get());
        grips.clear(); ag.BuildGrips(ae.get(),grips);
        auto as=ag.BeginDrag(ae.get(),grips[0]);
        ag.UpdateDrag(ae.get(),as.get(),grips[0],arc.Center+Math::Vec3{0,0,10},grips);
        DragEntityEntry entry;
        check(ag.EndDrag(ae.get(),as.get(),entry) && ae->GetArc().Center.z==350);
        ag.CancelDrag(ae.get(),as.get());
        ag.UpdateDrag(ae.get(),as.get(),grips[1],arc.PointAt(5.8),grips);
        check(near(ae->GetArc().StartPoint(),arc.PointAt(5.8)));
        ag.CancelDrag(ae.get(),as.get());
        ag.UpdateDrag(ae.get(),as.get(),grips[3],arc.Center+(arc.MidPoint()-arc.Center)*2,grips);
        check(std::abs(ae->GetArc().Radius-50)<1e-8);
        ag.CancelDrag(ae.get(),as.get());
        auto rotated=ae->Clone(900);
        RotateEntityInPlace(*rotated,{},.4);
        check(near(static_cast<ArcEntity*>(rotated.get())->GetArc().MidPoint(),RotatePoint(arc.MidPoint(),{},.4)));
        const MirrorAxis axis{{0,0,0},{1,0,0}};
        MirrorEntityInPlace(*rotated,axis);
        check(near(static_cast<ArcEntity*>(rotated.get())->GetArc().MidPoint(),ReflectPoint(RotatePoint(arc.MidPoint(),{},.4),axis)));
        const auto circleId=ce->GetID(), arcId=ae->GetID();
        scene.AddEntity(std::move(ce)); scene.AddEntity(std::move(ae));
        DragEntitiesCommand gripCommand({circleEntry});
        check(gripCommand.Execute(scene));
        check((static_cast<const CircleEntity*>(scene.GetEntity(circleId))->GetExtrusion()-circle.Normal).Length()<1e-8);
        gripCommand.Undo(scene);
        check((static_cast<const CircleEntity*>(scene.GetEntity(circleId))->GetExtrusion()-circle.Normal).Length()<1e-8);
        MoveCommand move({circleId,arcId},{10,20,30},scene);
        check(move.Execute(scene));
        check(near(static_cast<const ArcEntity*>(scene.GetEntity(arcId))->GetArc().MidPoint(),arc.MidPoint()+Math::Vec3{10,20,30}));
        move.Undo(scene);
        Document restored;
        check(restored.LoadFromString(document.SaveToString()));
        Document binary;
        check(binary.LoadFromString(document.SaveToString(true)));
        check(near(static_cast<const CircleEntity*>(binary.GetScene().GetEntity(circleId))->GetCircle().Center,circle.Center));
        Document legacy;
        check(legacy.LoadFromString(document.SaveToString()));
        auto* oldCircle=static_cast<CircleEntity*>(legacy.GetScene().GetEntity(circleId));
        auto* oldArc=static_cast<ArcEntity*>(legacy.GetScene().GetEntity(arcId));
        oldCircle->SetCenter(Math::WcsToOcs(oldCircle->GetOCS(),circle.Center));
        oldArc->SetCenter(Math::WcsToOcs(oldArc->GetOCS(),arc.Center));
        const auto oldText=std::regex_replace(legacy.SaveToString(),std::regex("\"worldCurve\"\\s*:\\s*true"),"\"worldCurve\":false");
        Document migrated;
        check(migrated.LoadFromString(oldText));
        check(near(static_cast<const CircleEntity*>(migrated.GetScene().GetEntity(circleId))->GetCircle().Center,circle.Center));
        check(near(static_cast<const ArcEntity*>(migrated.GetScene().GetEntity(arcId))->GetArc().MidPoint(),arc.MidPoint()));
        for (const auto kind : {CadFileKind::Dwg,CadFileKind::Dxf})
        {
            const auto data=ExportCad(restored.GetScene(),kind,CadSaveVersion::R2018);
            Scene imported; std::string error;
            check(!data.empty() && ImportCad(data,imported,&error));
            int count=0;
            imported.ForEachObject([&](const Object& object)
            {
                if (const auto* c=dynamic_cast<const CircleEntity*>(&object))
                { check(near(c->GetCircle().Center,circle.Center) && std::abs(c->GetCircle().Radius-25)<1e-8); ++count; }
                if (const auto* a=dynamic_cast<const ArcEntity*>(&object))
                { check(near(a->GetArc().MidPoint(),arc.MidPoint()) && near(a->GetArc().Center,arc.Center)); ++count; }
            });
            check(count==2);
        }
    }
}
