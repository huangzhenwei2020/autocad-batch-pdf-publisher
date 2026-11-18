#pragma once
#include "Document/Document.h"
#include "Document/Command/EntityMirror.h"
#include "Document/Command/EntityRotate.h"
#include "Editor/Grip/PolylineGripHandler.h"
#include "Import/CadExchange.h"
#include <filesystem>
#include <fstream>
#include <stdexcept>

inline void CheckWorkingPolyline()
{
    using namespace MiniCAD;
    const auto check = [](bool pass) { if (!pass) throw std::runtime_error("Working polyline invariant failed"); };
    const auto near = [](const Math::Point3& a, const Math::Point3& b) { return (a - b).Length() < 1e-7; };
    const Math::Point3 start{20, 30, 70};
    for (const Math::Vec3 normal : {Math::Vec3{0,-1,0}, {1,2,3}, {0,0,-1}})
    {
        const auto ocs = Math::BuildOCS(normal);
        const auto world = [&](const Math::Point3& local) { return Math::OcsToWcs(ocs, local); };
        const auto highPoint = world({170, 80, 70});
        Polyline path({world(start), world({120,30,70}), world({220,30,70}), world(start)}, {0,-1,0});
        path.Normal = ocs.Uz;
        check(near(PolylineOps::PointAt(path, 1.5), highPoint));
        check(near(path.ClosestPoint(highPoint + ocs.Uz * 20), highPoint));
        check(std::abs(path.Length() - (300 + 50 * Math::PI)) < 1e-8);
        AABB bounds = path.GetBounds();
        for (const auto& point : path.Tessellate(Math::PI / 180.0))
            check(point.x >= bounds.Min.x - 1e-8 && point.x <= bounds.Max.x + 1e-8 &&
                  point.y >= bounds.Min.y - 1e-8 && point.y <= bounds.Max.y + 1e-8 &&
                  point.z >= bounds.Min.z - 1e-8 && point.z <= bounds.Max.z + 1e-8);

        Document document;
        auto entity = std::make_unique<PolylineEntity>(document.GetScene().NextObjectID(), path);
        entity->SetWidth(.25);
        PolylineGripHandler handler;
        std::vector<Grip> grips;
        handler.BuildGrips(entity.get(), grips);
        const Grip vertex = grips.front();
        auto drag = handler.BeginDrag(entity.get(), vertex);
        handler.UpdateDrag(entity.get(), drag.get(), vertex, world({10,20,99}), grips);
        const auto& edited = entity->GetPolyline();
        check(near(edited.Points.front(), world({10,20,70})) && PolylineOps::IsRing(edited));
        DragEntityEntry entry;
        check(handler.EndDrag(entity.get(), drag.get(), entry));
        handler.CancelDrag(entity.get(), drag.get());
        const auto id = entity->GetID();
        auto transformed = entity->Clone(id);
        RotateEntityInPlace(*transformed, {}, .4);
        const MirrorAxis axis{{0,0,0},{1,0,0}};
        MirrorEntityInPlace(*transformed, axis);
        check(near(PolylineOps::PointAt(static_cast<PolylineEntity&>(*transformed).GetPolyline(), 1.5),
            ReflectPoint(RotatePoint(highPoint, {}, .4), axis)));
        document.GetScene().AddEntity(std::move(entity));
        DragEntitiesCommand command({entry});
        check(command.Execute(document.GetScene()));
        command.Undo(document.GetScene());

        Document restored;
        check(restored.LoadFromString(document.SaveToString()));
        check(near(PolylineOps::PointAt(static_cast<const PolylineEntity*>(restored.GetScene().GetEntity(id))->GetPolyline(), 1.5), highPoint));
        for (auto kind : {CadFileKind::Dwg, CadFileKind::Dxf})
        {
            const auto data = ExportCad(restored.GetScene(), kind, CadSaveVersion::R2018);
            Scene imported;
            check(!data.empty() && ImportCad(data, imported));
            int found = 0;
            imported.ForEachObject([&](const Object& object)
            {
                const auto* polyline = dynamic_cast<const PolylineEntity*>(&object);
                if (!polyline) return;
                const auto& actual = polyline->GetPolyline();
                check(PolylineOps::IsRing(actual) && near(actual.Points.front(), world(start)) &&
                    near(PolylineOps::PointAt(actual, 1.5), highPoint) && std::abs(polyline->GetWidth() - .25) < 1e-8);
                ++found;
            });
            check(found == 1);
        }
    }
}

inline void CheckWorkingPolylineFiles(const std::filesystem::path& root)
{
    using namespace MiniCAD;
    const auto check = [](bool pass, const char* message) { if (!pass) throw std::runtime_error(message); };
    for (int view : {2, 6, 1})
    {
        Math::Point3 origin;
        Math::Vec3 x, y;
        std::ifstream plane(root / ("polyline-plane-" + std::to_string(view) + ".txt"));
        plane >> origin.x >> origin.y >> origin.z >> x.x >> x.y >> x.z >> y.x >> y.y >> y.z;
        check(!plane.fail(), "Working polyline plane fixture missing");
        const auto normal = Math::Cross(x, y);
        auto world = [&](double u, double v, double elevation)
        { return origin + x * u + y * v + normal * elevation; };
        for (bool incremental : {false, true})
        for (auto extension : {L".dwg", L".dxf"})
        {
            const auto name = std::wstring(incremental ? L"工作平面多段线增量-" : L"工作平面多段线-") +
                std::to_wstring(view) + extension;
            std::ifstream file(root / name, std::ios::binary);
            const std::vector<std::uint8_t> bytes{std::istreambuf_iterator<char>(file), {}};
            Scene scene;
            check(ImportCad(bytes, scene), "Working polyline output could not be read");
            const double elevation = incremental ? 22 : 12;
            int count = 0;
            scene.ForEachObject([&](const Object& object)
            {
                const auto* polyline = dynamic_cast<const PolylineEntity*>(&object);
                if (!polyline) return;
                const auto& path = polyline->GetPolyline();
                check(path.Points.size() == 4 && PolylineOps::IsRing(path), "Working polyline did not retain closure");
                const double offset = (path.Points.front() - world(30, 50, elevation)).Length() < 1e-7 ? 0 : 250;
                check((path.Points[0] - world(30 + offset, 50, elevation)).Length() < 1e-7 &&
                      (path.Points[1] - world(130 + offset, 50, elevation)).Length() < 1e-7 &&
                      (path.Points[2] - world(230 + offset, 50, elevation)).Length() < 1e-7,
                    "Working polyline world coordinates changed");
                check((PolylineOps::PointAt(path, 1.5) - world(180 + offset, 100, elevation)).Length() < 1e-7 &&
                      std::abs(polyline->GetWidth() - .25) < 1e-8 &&
                      std::abs(Math::Dot(path.Normal, normal)) > 1 - 1e-8,
                    "Working polyline arc, width or normal changed");
                ++count;
            });
            check(count == 2, "Working polyline output count changed");
        }
    }
}
