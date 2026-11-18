#pragma once
#include "Document/Document.h"
#include "Document/Command/EntityMirror.h"
#include "Document/Command/EntityRotate.h"
#include "Editor/Grip/EllipseGripHandler.h"
#include "Import/CadExchange.h"
#include <filesystem>
#include <fstream>
#include <stdexcept>

inline void CheckWorkingEllipse()
{
    using namespace MiniCAD;
    auto check = [](bool condition, const char* message) { if (!condition) throw std::runtime_error(message); };
    auto near = [](const Math::Point3& a, const Math::Point3& b) { return (a - b).Length() < 1e-7; };
    for (const auto normal : {Math::Vec3{0,0,1}, {0,-1,0}, {1,2,3}, {0,0,-1}})
    for (bool arc : {false, true})
    {
        const auto frame = Math::BuildOCS(normal);
        Ellipse ellipse{{20,30,70}, arc ? 20.0 : 50.0, arc ? 40.0 : 20.0, .4, 0, Math::TwoPI, frame.Uz};
        if (arc) ellipse.SetParams(5.2, .8);
        const auto [x, y] = ellipse.Axes();
        check(std::abs(Math::Dot(x, frame.Uz)) < 1e-10 && Math::Dot(Math::Cross(x,y), frame.Uz) > 1 - 1e-10, "Ellipse plane basis is invalid");
        const auto mid = ellipse.PointAt(ellipse.MidParam());
        check(near(ellipse.ClosestPoint(mid + frame.Uz * 25), mid), "Ellipse nearest point left its plane");
        const double dt = 1e-5;
        const auto tangent = (ellipse.PointAt(ellipse.MidParam() + dt) - ellipse.PointAt(ellipse.MidParam() - dt)) / (2 * dt);
        check((EllipseCurve(ellipse).TangentAt(ellipse.MidParam()) - tangent).Length() < 1e-7, "Ellipse tangent changed planes");
        const auto bounds = ellipse.GetBounds();
        for (int i = 0; i <= 360; ++i)
        {
            const auto p = ellipse.PointAt(ellipse.StartParam + ellipse.SweepParam() * i / 360);
            check(p.x >= bounds.Min.x - 1e-8 && p.x <= bounds.Max.x + 1e-8 &&
                  p.y >= bounds.Min.y - 1e-8 && p.y <= bounds.Max.y + 1e-8 &&
                  p.z >= bounds.Min.z - 1e-8 && p.z <= bounds.Max.z + 1e-8, "Ellipse lies outside its bounds");
        }

        const auto rotated = RotateEllipse(ellipse, {}, .6);
        const MirrorAxis axis{{0,0,0},{1,1,0}};
        const auto mirrored = MirrorEllipse(rotated, axis);
        check(near(rotated.PointAt(rotated.MidParam()), RotatePoint(mid, {}, .6)) &&
              near(mirrored.PointAt(mirrored.MidParam()), ReflectPoint(RotatePoint(mid, {}, .6), axis)),
              "Ellipse rotate/mirror changed its shape");

        Document document;
        const auto id = document.GetScene().NextObjectID();
        auto entity = std::make_unique<EllipseEntity>(id, ellipse);
        EllipseGripHandler handler;
        std::vector<Grip> grips;
        handler.BuildGrips(entity.get(), grips);
        const auto center = grips.front();
        auto drag = handler.BeginDrag(entity.get(), center);
        handler.UpdateDrag(entity.get(), drag.get(), center, ellipse.Center + Math::Vec3{0,0,10}, grips);
        DragEntityEntry entry;
        check(handler.EndDrag(entity.get(), drag.get(), entry), "Pure Z ellipse grip edit was not recorded");
        handler.CancelDrag(entity.get(), drag.get());

        if (!arc)
        {
            grips.clear();
            handler.BuildGrips(entity.get(), grips);
            const auto quadrant = grips[1];
            drag = handler.BeginDrag(entity.get(), quadrant);
            handler.UpdateDrag(entity.get(), drag.get(), quadrant, ellipse.Center + x * 80 + frame.Uz * 10, grips);
            check(std::abs(entity->GetEllipse().RadiusX - 80) < 1e-8, "Ellipse axis grip changed the wrong radius");
            handler.CancelDrag(entity.get(), drag.get());
        }
        else
        {
            auto quarter = ellipse;
            quarter.SetParams(0, Math::PI / 2);
            entity->SetEllipse(quarter);
            grips.clear();
            handler.BuildGrips(entity.get(), grips);
            check(grips.size() == 3, "Ellipse arc endpoint and axis grips overlap");
            const auto start = grips[1];
            drag = handler.BeginDrag(entity.get(), start);
            handler.UpdateDrag(entity.get(), drag.get(), start, quarter.EndPoint(), grips);
            check(!entity->GetEllipse().IsFull(), "Coincident ellipse arc endpoints turned into a full ellipse");
            handler.UpdateDrag(entity.get(), drag.get(), start, quarter.PointAt(.3), grips);
            check(near(entity->GetEllipse().StartPoint(), quarter.PointAt(.3)), "Ellipse arc grip changed the wrong endpoint");
            entity->SetEllipse(ellipse);
        }
        document.GetScene().AddEntity(std::move(entity));
        DragEntitiesCommand command({entry});
        check(command.Execute(document.GetScene()), "Ellipse grip command failed");
        command.Undo(document.GetScene());
        Document restored;
        check(restored.LoadFromString(document.SaveToString()), "Ellipse native serialization failed");
        const auto& internal = static_cast<const EllipseEntity*>(restored.GetScene().GetEntity(id))->GetEllipse();
        check(near(internal.PointAt(internal.MidParam()), mid), "Ellipse internal round trip changed its geometry");

        for (const auto kind : {CadFileKind::Dwg, CadFileKind::Dxf})
        {
            const auto data = ExportCad(restored.GetScene(), kind, CadSaveVersion::R2018);
            Scene imported;
            check(!data.empty() && ImportCad(data, imported), "Ellipse DWG/DXF round trip failed");
            int count = 0;
            imported.ForEachObject([&](const Object& object)
            {
                const auto* e = dynamic_cast<const EllipseEntity*>(&object);
                if (!e) return;
                const auto& actual = e->GetEllipse();
                check(near(actual.Center, ellipse.Center) && actual.IsFull() == !arc &&
                      std::abs(Math::Dot(Math::BuildOCS(actual.Normal).Uz, frame.Uz)) > 1 - 1e-8,
                      "Ellipse center or normal changed during exchange");
                if (arc)
                    check(near(actual.PointAt(actual.MidParam()), mid) &&
                          (near(actual.StartPoint(), ellipse.StartPoint()) || near(actual.StartPoint(), ellipse.EndPoint())),
                          "Ellipse arc changed after major-axis normalization");
                else
                    check(near(actual.VertexE(),ellipse.VertexE()) &&
                          (near(actual.VertexN(),ellipse.VertexN()) || near(actual.VertexN(),ellipse.VertexS())),
                          "Full ellipse axes changed during exchange");
                ++count;
            });
            check(count == 1, "Expected a single native ellipse");
        }
    }
}

inline void CheckWorkingEllipseFiles(const std::filesystem::path& root)
{
    using namespace MiniCAD;
    const auto check = [](bool ok, const char* message) { if (!ok) throw std::runtime_error(message); };
    const auto near = [](Math::Point3 a, Math::Point3 b) { return (a - b).Length() < 1e-7; };
    for (int view : {2, 6, 1})
    {
        Math::Point3 origin;
        Math::Vec3 x, y;
        std::ifstream plane(root / ("ellipse-plane-" + std::to_string(view) + ".txt"));
        plane >> origin.x >> origin.y >> origin.z >> x.x >> x.y >> x.z >> y.x >> y.y >> y.z;
        check(!plane.fail(), "Working ellipse plane fixture missing");
        const auto normal = Math::Cross(x, y);
        const auto world = [&](double u, double v, double elevation) { return origin + x * u + y * v + normal * elevation; };
        for (bool incremental : {false, true})
        for (auto extension : {L".dwg", L".dxf"})
        {
            const auto name = std::wstring(incremental ? L"工作平面椭圆增量-" : L"工作平面椭圆-") +
                std::to_wstring(view) + extension;
            std::ifstream file(root / name, std::ios::binary);
            const std::vector<std::uint8_t> bytes{std::istreambuf_iterator<char>(file), {}};
            Scene scene;
            check(ImportCad(bytes, scene), "Working ellipse output could not be opened");
            const double z = incremental ? 22 : 12;
            bool seen[2][2]{};
            int count = 0;
            scene.ForEachObject([&](const Object& object)
            {
                const auto* e = dynamic_cast<const EllipseEntity*>(&object);
                check(e != nullptr, "Working ellipse output contains a different entity");
                const auto& geometry = e->GetEllipse();
                const int full = geometry.IsFull() ? 1 : 0;
                const double u = full ? 50 : 210, v = full ? 80 : 120;
                const int copy = near(geometry.Center, world(u,v,z)) ? 0 : 1;
                const double offset = copy * 300;
                check(!seen[full][copy] && near(geometry.Center, world(u + offset,v,z)) &&
                      std::abs(Math::Dot(Math::BuildOCS(geometry.Normal).Uz, normal)) > 1 - 1e-8,
                      "Working ellipse center or normal changed");
                seen[full][copy] = true;
                check(std::abs(geometry.RadiusX - (full ? 50 : 40)) < 1e-8 &&
                      std::abs(geometry.RadiusY - 20) < 1e-8, "Working ellipse radii changed");
                if (full)
                    check(near(geometry.VertexE(),world(80+offset,120,z)), "Working ellipse major axis changed");
                else
                {
                    const auto a = world(242+offset,96,z), b = world(222+offset,136,z);
                    const auto start = geometry.StartPoint(), end = geometry.EndPoint();
                    check((near(start,a) && near(end,b)) || (near(start,b) && near(end,a)), "Working ellipse arc endpoints changed");
                    check(near(geometry.PointAt(geometry.MidParam()),
                               world(210+offset+44/std::sqrt(2.0),120-8/std::sqrt(2.0),z)), "Working ellipse arc direction changed");
                }
                ++count;
            });
            check(count == 4, "Working ellipse output count changed");
        }
    }
}
