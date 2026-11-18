#pragma once
#include "Document/Document.h"
#include "Document/Command/EntityTranslate.h"
#include "Document/Command/MoveCommand.h"
#include "Editor/Grip/HatchGripHandler.h"
#include "Editor/Tools/HatchBoundaryFinder.hpp"
#include "Import/CadExchange.h"
#include <stdexcept>

inline void CheckWorkingHatch()
{
    using namespace MiniCAD;
    auto check = [](bool condition, const char* message) { if (!condition) throw std::runtime_error(message); };
    struct Capture : IDrawSink
    {
        std::vector<Math::Point3> Points;
        void DrawLine(const Math::Point3& a, const Math::Point3& b, const Math::Color4&, bool) override
        { Points.insert(Points.end(), {a,b}); }
        void FillTriangle(const Math::Point3& a, const Math::Point3& b, const Math::Point3& c, const Math::Color4&) override
        { Points.insert(Points.end(), {a,b,c}); }
    };
    for (const auto normal : {Math::Vec3{0,0,1}, {0,-1,0}, {1,2,3}, {0,0,-1}})
    {
        const auto frame = Math::BuildOCS(normal);
        auto world = [&](const Math::Point3& p) { return Math::OcsToWcs(frame,p); };
        Scene source;
        // A circular arc and chord form a closed half disk. A smaller loop at another height
        // projects over the seed but cannot serve as its boundary.
        auto arc = std::make_unique<ArcEntity>(source.NextObjectID(), world({30,40,70}), 20, 0, Math::PI);
        arc->SetExtrusion(frame.Uz);
        source.AddEntity(std::move(arc));
        source.AddEntity(std::make_unique<LineEntity>(source.NextObjectID(), world({10,40,70}), world({50,40,70})));
        Polyline other({world({29,48,90}),world({31,48,90}),world({31,52,90}),world({29,52,90}),world({29,48,90})});
        other.Normal = frame.Uz;
        source.AddEntity(std::make_unique<PolylineEntity>(source.NextObjectID(),other));
        HatchLoop loop;
        check(!HatchBoundary::FindEnclosingLoop(source,{30,30,70},loop,frame), "Hatch selected the wrong side of an arc");
        check(HatchBoundary::FindEnclosingLoop(source,{30,50,70},loop,frame), "Hatch could not find coplanar arc and chord");
        check(loop.Edges.size() == 1 && PolylineOps::IsRing(loop.Edges[0].Poly) &&
              std::any_of(loop.Edges[0].Poly.Bulges.begin(), loop.Edges[0].Poly.Bulges.end(),
                   [](double bulge) { return std::abs(bulge) > .9; }), "Hatch lost closure or analytic arc");
        for (const auto& p : loop.Tessellate())
            check(std::abs(p.z - 70) < 1e-8 && p.y >= 40-1e-8, "Hatch included a different plane or flipped the arc");

        for (bool solid : {false,true})
        {
            Document drawing;
            auto& scene = drawing.GetScene();
            const auto id = scene.NextObjectID();
            const auto pattern = solid ? HatchPattern::MakeSolid() : HatchPattern::ANSI31(3);
            auto hatch = std::make_unique<HatchEntity>(id, std::vector<HatchLoop>{loop}, pattern);
            hatch->SetExtrusion(frame.Uz);
            hatch->SetScale(2);
            hatch->SetAngle(17);
            Capture sink;
            hatch->Draw(sink,false,false);
            check(!sink.Points.empty(), "Working hatch produced no geometry");
            for (const auto& p : sink.Points)
                check(std::abs(Math::WcsToOcs(frame,p).z - 70) < 1e-7, "Hatch rendering was flattened into XY");
            HatchGripHandler handler;
            std::vector<Grip> grips;
            handler.BuildGrips(hatch.get(),grips);
            const auto first = grips.front();
            auto grip = handler.BeginDrag(hatch.get(),first);
            handler.UpdateDrag(hatch.get(),grip.get(),first,first.WorldPos+frame.Ux*2+frame.Uz*10,grips);
            check(PolylineOps::IsRing(hatch->GetLoops().front().Edges.front().Poly), "Hatch vertex grip opened a closed boundary");
            handler.CancelDrag(hatch.get(),grip.get());
            const auto& analytic = loop.Edges.front().Poly;
            int arcSegment = 0;
            while (arcSegment < analytic.SegCount() && !analytic.SegIsArc(arcSegment)) ++arcSegment;
            const auto midpoint = std::find_if(grips.begin(), grips.end(), [&](const Grip& value)
            { return value.GripType == Grip::Type::Mid && value.SubIndex == arcSegment; });
            check(midpoint != grips.end(), "Hatch arc grip was not available");
            const auto arcGrip = *midpoint;
            grip = handler.BeginDrag(hatch.get(),arcGrip);
            handler.UpdateDrag(hatch.get(),grip.get(),arcGrip,world({30,55,70}),grips);
            const auto& changed = hatch->GetLoops().front().Edges.front().Poly;
            check((PolylineOps::PointAt(changed,arcSegment+.5)-Math::Point3{30,55,70}).Length() < 1e-7,
                  "Hatch midpoint grip changed arc direction");
            handler.CancelDrag(hatch.get(),grip.get());
            scene.AddEntity(std::move(hatch));
            const auto movement = frame.Ux * 4 + frame.Uy * 6 + frame.Uz * 7;
            MoveCommand move({id}, movement, scene);
            check(move.Execute(scene), "Hatch move failed");
            const auto& moved = static_cast<const HatchEntity*>(scene.GetEntity(id))->GetLoops().front();
            check(std::abs(moved.Edges.front().StartPt().z - 77) < 1e-7, "Hatch move lost local elevation");
            move.Undo(scene);
            Document restored;
            check(restored.LoadFromString(drawing.SaveToString()), "Hatch internal serialization failed");
            for (const auto kind : {CadFileKind::Dwg, CadFileKind::Dxf})
            {
                const auto bytes = ExportCad(restored.GetScene(),kind,CadSaveVersion::R2018);
                Scene imported;
                check(!bytes.empty() && ImportCad(bytes,imported), "Working hatch DWG/DXF could not reopen");
                int count = 0;
                imported.ForEachObject([&](const Object& object)
                {
                    const auto* value = dynamic_cast<const HatchEntity*>(&object);
                    if (!value) return;
                    check(Math::Dot(value->GetOCS().Uz,frame.Uz) > 1-1e-8 &&
                          value->GetPattern().Solid == solid && value->GetLoops().size() == 1, "Hatch normal or style changed");
                    const auto& boundary = value->GetLoops().front().Edges.front().Poly;
                    check(PolylineOps::IsRing(boundary) && std::abs(boundary.Points.front().z-70)<1e-7, "Hatch boundary changed in exchange");
                    if (!solid)
                        check(std::abs(value->GetScale()-2)<1e-8 && std::abs(value->GetAngle()-17)<1e-8,
                              "Hatch pattern scale or angle changed");
                    ++count;
                });
                check(count == 1, "Expected a single native HATCH");
            }
        }
    }
}
