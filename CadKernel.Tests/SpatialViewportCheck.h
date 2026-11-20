#pragma once
#include "Core/Entity/PointEntity.hpp"
#include "Document/Document.h"
#include "Document/PerspectiveViewport.hpp"
#include <fstream>
#include <iomanip>

inline void WriteSpatialViewportFixture(const std::filesystem::path& root)
{
    using namespace MiniCAD;
    Document document;
    auto save = [&](const wchar_t* name)
    {
        const auto path = (root / name).u8string();
        return document.SaveAs(std::string(path.begin(), path.end()));
    };
    auto& scene = document.GetScene();
    DrawingLayout paper;
    paper.Name = "Layout1";
    paper.Model = false;
    paper.Order = 1;
    scene.SetLayouts({DrawingLayout{}, paper});
    scene.AddEntity(std::make_unique<LineEntity>(scene.NextObjectID(), Math::Point3{100,200,300}, Math::Point3{200,200,400}));
    const Math::Point3 directions[] = {{0,-2,0}, {1,2,3}, {0,0,-1}, {.01,.005,1}};
    uint64_t firstViewport = 0;
    std::ofstream projections(root / "spatial-projection.txt");
    projections << std::setprecision(17);
    for (int i = 0; i < 4; ++i)
    {
        auto viewport=std::make_unique<ViewportEntity>(scene.NextObjectID(),Math::Point3{150.+300*i,100,0},300,200);
        if (i == 0) firstViewport = viewport->GetID();
        viewport->SetViewportId(i+2); viewport->SetViewHeight(2000); viewport->SetPlanView(false);
        viewport->SetTwist(i*.3);
        if (i == 3)
        {
            viewport->SetSpatialView(directions[i], {}, {});
            viewport->SetTwist(0);
            const auto [right, up] = viewport->ViewAxes();
            if (right.x < .999 || std::abs(right.y) > 1e-8 || up.y < .999)
                throw std::runtime_error("Near-Z arbitrary axes differ from AutoCAD");
            viewport->SetTwist(i * .3);
        }
        viewport->SetSpatialView(directions[i],{100,200,300},{50,40,0});
        viewport->GetAttr().InPaperSpace=true; viewport->GetAttr().LayoutKey=scene.Layouts()[1].OwnerKey;
        const auto point=viewport->ModelToPaper({100,200,300});
        if (std::hypot(point.x-(145+300*i),point.y-96)>1e-8) throw std::runtime_error("Spatial viewport target projection failed");
        if (i==0)
        {
            const auto end=viewport->ModelToPaper({200,200,400});
            if(std::hypot(end.x-155,end.y-106)>1e-8) throw std::runtime_error("Elevation Z projection failed");
        }
        const auto dcs=viewport->SpatialViewCenterDcs();
        if (std::hypot(dcs.x-50,dcs.y-40)>1e-8) throw std::runtime_error("Spatial viewport DCS inverse failed");
        const Math::Point3 paperPoint{170.0 + 300 * i, 130, 0};
        const auto roundtrip = viewport->ModelToPaper(viewport->PaperToModel(paperPoint));
        if (std::hypot(roundtrip.x - paperPoint.x, roundtrip.y - paperPoint.y) > 1e-8)
            throw std::runtime_error("Spatial viewport paper inverse failed");
        const auto anchor = viewport->PaperToModel(paperPoint);
        auto zoomed = viewport->Clone(viewport->GetID());
        auto* zoomedViewport = static_cast<ViewportEntity*>(zoomed.get());
        const auto offset = viewport->PaperVectorToModel(20, 30);
        const double factor = 1 / 1.2;
        zoomedViewport->SetViewCenter(viewport->GetViewCenter() + offset * (1 - factor));
        zoomedViewport->SetViewHeight(viewport->GetViewHeight() * factor);
        const auto zoomAnchor = zoomedViewport->ModelToPaper(anchor);
        if (std::hypot(zoomAnchor.x - paperPoint.x, zoomAnchor.y - paperPoint.y) > 1e-8)
            throw std::runtime_error("Spatial viewport zoom anchor moved");
        const auto projected = viewport->ModelToPaper({200,200,400});
        projections << projected.x << ' ' << projected.y << '\n';
        scene.AddEntity(std::move(viewport));
    }
    const auto serialized=document.SaveToString();
    Document restored;
    if (!restored.LoadFromString(serialized)) throw std::runtime_error("Spatial viewport serialization failed");
    int count=0;
    restored.GetScene().ForEachObject([&](const Object& object)
    {
        const auto* viewport=dynamic_cast<const ViewportEntity*>(&object);
        if (!viewport) return;
        if (!viewport->HasSpatialView() || std::abs(viewport->GetViewTarget().z-300)>1e-8) throw std::runtime_error("Spatial viewport fields lost");
        ++count;
    });
    if (count!=4 || !save(L"spatial-viewport.dwg")) throw std::runtime_error("Spatial viewport fixture failed");
    if (!projections) throw std::runtime_error("Spatial viewport projection fixture could not be written");
    Camera camera(1200, 800);
    auto state = camera.GetState();
    document.SetActiveLayout(1, state);
    if (!document.ActiveLayoutIsPaper() || document.ActiveLayout() != 1)
        throw std::runtime_error("Could not activate the spatial edit fixture layout");
    const auto paperState = state;
    for (int i = 0; i < 4; ++i)
    {
        const auto id = firstViewport + i;
        const auto* viewport = static_cast<const ViewportEntity*>(scene.GetEntity(id));
        if (!viewport || !document.BeginViewportEdit(id, state, 1.5, true) ||
            !document.EndViewportEdit(state) || state.Target != paperState.Target || state.Zoom != paperState.Zoom ||
            !document.BeginViewportEdit(id, state, 1.5, false))
            throw std::runtime_error("Spatial viewport edit modes did not preserve the paper camera");
        camera.SetState(state);
        for (const Math::Point3 point : {Math::Point3{100,200,300}, {200,200,400}, {-15,60,780}})
        {
            const auto paper = viewport->ModelToPaper(point);
            const auto screen = camera.WorldToScreen(point);
            const double pixelsPerPaperUnit = 800 / (state.Zoom * viewport->ModelToPaperScale());
            const double x = 600 + (paper.x - viewport->GetCenter().x) * pixelsPerPaperUnit;
            const double y = 400 - (paper.y - viewport->GetCenter().y) * pixelsPerPaperUnit;
            if (std::hypot(screen.x - x, screen.y - y) > 1e-8)
                throw std::runtime_error("Maximized camera differs from the layout viewport projection");
        }
        const auto point = viewport->GetViewCenter() + viewport->PaperVectorToModel(30, 45);
        const auto screen = camera.WorldToScreen(point);
        camera.Zoom(2, screen.x, screen.y);
        const auto after = camera.WorldToScreen(point);
        if (std::hypot(after.x - screen.x, after.y - screen.y) > 1e-8 ||
            std::abs(Math::Dot(camera.GetTarget() - state.Target, camera.GetViewDir())) > 1e-8)
            throw std::runtime_error("Spatial camera zoom changed its screen anchor or depth");
        state = camera.GetState();
        if (!document.EndViewportEdit(state) || state.Target != paperState.Target ||
            state.Zoom != paperState.Zoom || state.Roll != paperState.Roll)
            throw std::runtime_error("Returning from spatial editing changed the paper camera");
    }
    document.SetActiveLayout(0, state);
    const auto* source = dynamic_cast<const ViewportEntity*>(scene.GetEntity(firstViewport));
    if (!source) throw std::runtime_error("Spatial viewport fixture missing");
    auto perspective = source->Clone(scene.NextObjectID());
    static_cast<ViewportEntity*>(perspective.get())->SetPerspective(true);
    static_cast<ViewportEntity*>(perspective.get())->SetCenter({1350,100,0});
    if (static_cast<const ViewportEntity*>(perspective.get())->SupportsModelEdit(false))
        throw std::runtime_error("Perspective viewport was offered as orthographic editing");
    scene.AddEntity(std::move(perspective));
    if (!save(L"perspective-viewport.dwg"))
        throw std::runtime_error("Perspective viewport fixture failed");
}

inline void WriteSpatialInlineClipFixture(const std::filesystem::path& root)
{
    using namespace MiniCAD;
    Document document;
    auto& scene = document.GetScene();
    DrawingLayout paper;
    paper.Name = "Layout1";
    paper.Model = false;
    paper.Order = 1;
    scene.SetLayouts({DrawingLayout{}, paper});
    auto viewport = std::make_unique<ViewportEntity>(scene.NextObjectID(), Math::Point3{150,100,0}, 300, 200);
    viewport->SetViewportId(2);
    viewport->SetViewHeight(2000);
    viewport->SetPlanView(false);
    viewport->SetLocked(false);
    viewport->SetSpatialView({0,-1,0}, {100,200,300}, {});
    std::vector<Math::Point3> clip{{0,0,0},{300,0,0},{300,200,0},{180,200,0},
        {180,120,0},{120,120,0},{120,200,0},{0,200,0}};
    for (auto& point : clip)
        point = {(point.x - 150) / 300, (point.y - 100) / 200, 0};
    viewport->SetClipCoordinates(std::move(clip));
    viewport->GetAttr().InPaperSpace = true;
    viewport->GetAttr().LayoutKey = scene.Layouts()[1].OwnerKey;
    auto line = [&](double x, double y, double endX, double endY)
    {
        scene.AddEntity(std::make_unique<LineEntity>(scene.NextObjectID(),
            viewport->PaperToModel({x,y,0}), viewport->PaperToModel({endX,endY,0})));
    };
    line(60,150,240,150);
    line(138,145,160,160);
    line(105,175,145,175);
    scene.AddEntity(std::move(viewport));
    const auto path = (root / L"spatial-inline-clip.dwg").u8string();
    if (!document.SaveAs(std::string(path.begin(), path.end())))
        throw std::runtime_error("Could not write non-planar inline clip drawing");
}

inline void WritePerspectiveProjectionFixture(const std::filesystem::path& root)
{
    using namespace MiniCAD;
    Document document;
    auto& scene = document.GetScene();
    DrawingLayout paper;
    paper.Name = "Layout1";
    paper.Model = false;
    paper.Order = 1;
    scene.SetLayouts({DrawingLayout{}, paper});
    auto viewport = std::make_unique<ViewportEntity>(scene.NextObjectID(), Math::Point3{150,100,0}, 300, 200);
    viewport->SetViewportId(2);
    viewport->SetViewHeight(2000);
    viewport->SetPerspective(true);
    viewport->SetTwist(.2);
    viewport->SetSpatialView({0,0,1000}, {100,200,300}, {10,-15,0});
    viewport->SetCameraOptics({42, 600, -400, true, true, false});
    viewport->GetAttr().InPaperSpace = true;
    viewport->GetAttr().LayoutKey = scene.Layouts()[1].OwnerKey;
    const auto center = viewport->GetViewCenter();
    const auto [right, up] = viewport->ViewAxes();
    const auto normal = Math::Cross(right, up);
    auto world = [&](double x, double y, double z) { return center + right*x + up*y + normal*z; };
    auto line = [&](Math::Point3 a, Math::Point3 b, uint16_t color)
    {
        auto entity = std::make_unique<LineEntity>(scene.NextObjectID(), a, b);
        entity->GetAttr().Color = EntityColor::FromAci(color);
        scene.AddEntity(std::move(entity));
    };
    line(world(-120,50,300), world(120,50,300), 1);       // near, red
    line(world(-120,-80,-200), world(120,-80,-200), 3);  // far, green
    line(world(-120,0,800), world(120,0,800), 5);       // in front of the clip
    line(world(-120,0,-600), world(120,0,-600), 2);     // behind the back clip

    const PerspectiveViewport projection(*viewport);
    auto point = [&](double x, double y, double z)
    { return Vertex_P3_C4{ToRenderFloat3(world(x,y,z), center), {1,1,1,1}}; };
    auto nearPoint = point(120,50,300);
    auto farPoint = point(120,-80,-200);
    const auto lens = std::hypot(300., 200.);
    if (!projection.Valid() || !projection.Point(nearPoint, {}) || !projection.Point(farPoint, {}) ||
        std::abs(nearPoint.pos.x - (150 + 120*lens/700)) > 1e-4 ||
        std::abs(farPoint.pos.y - (100 - 80*lens/1200)) > 1e-4 ||
        projection.Point(nearPoint = point(0,0,800), {}) || projection.Point(farPoint = point(0,0,-600), {}))
        throw std::runtime_error("Perspective depth, scale or front/back clipping failed");
    const auto offset = projection.TargetPlaneOffset(25, -15);
    auto targetAnchor = Vertex_P3_C4{ToRenderFloat3(center + offset, center), {1,1,1,1}};
    if (!projection.Point(targetAnchor, {}) ||
        std::hypot(targetAnchor.pos.x - 175, targetAnchor.pos.y - 85) > 1e-4)
        throw std::runtime_error("Perspective navigation did not use the target plane projection");
    Math::WorkingPlane tilted;
    tilted.Set(center, right + normal, up);
    const auto onPlane = tilted.ToWorld({20, 30, 0});
    const auto paperPoint = projection.ModelPointToPaper(onPlane);
    const auto picked = paperPoint ? projection.PaperPointOnPlane(*paperPoint, tilted) : std::nullopt;
    Math::WorkingPlane parallel;
    parallel.Set(center, right, normal);
    Math::WorkingPlane clipped;
    clipped.Origin = world(0, 0, -600);
    if (!picked || (*picked - onPlane).Length() > 1e-8 ||
        projection.PaperPointOnPlane(viewport->GetCenter(), parallel) ||
        projection.PaperPointOnPlane(viewport->GetCenter(), clipped))
        throw std::runtime_error("Perspective ray intersection did not respect the working plane and camera clipping");
    auto a = point(30,0,0), b = point(30,0,1500);
    if (!projection.Line(a, b, {}) || std::abs(b.pos.x - (150 + 30*lens/400)) > 1e-4)
        throw std::runtime_error("A segment crossing the front clip was not truncated correctly");
    auto noDepthClip = viewport->Clone(viewport->GetID());
    auto& eyeViewport = static_cast<ViewportEntity&>(*noDepthClip);
    eyeViewport.SetCameraOptics({42});
    const PerspectiveViewport eye(eyeViewport);
    a = point(100,0,0); b = point(100,0,1500);
    if (!eye.Line(a,b,{}) || std::abs(b.pos.x-300) > 1e-3)
        throw std::runtime_error("A segment crossing the eye plane escaped the frustum");
    int triangles = 0;
    projection.Triangle(point(-40,0,0), point(40,0,0), point(0,50,900), {},
        [&](const auto&, const auto&, const auto&) { ++triangles; });
    if (triangles != 2)
        throw std::runtime_error("A triangle crossing the perspective near plane was lost");
    scene.AddEntity(std::move(viewport));
    Document restored;
    if (!restored.LoadFromString(document.SaveToString()))
        throw std::runtime_error("Perspective document could not be serialized");
    int restoredViews = 0;
    restored.GetScene().ForEachObject([&](const Object& object)
    {
        const auto* view = dynamic_cast<const ViewportEntity*>(&object);
        if (!view) return;
        const auto& optics = view->GetCameraOptics();
        if (!view->IsPerspective() || !view->HasSpatialView() || optics.Lens != 42 ||
            optics.Front != 600 || optics.Back != -400 || !optics.FrontEnabled || !optics.BackEnabled || optics.FrontAtEye)
            throw std::runtime_error("Perspective optics did not survive internal serialization");
        ++restoredViews;
    });
    const auto path = (root / L"perspective-projection.dwg").u8string();
    if (restoredViews != 1 || !document.SaveAs(std::string(path.begin(), path.end())))
        throw std::runtime_error("Could not save the perspective fixture");
}

inline void CheckSpatialNavigationFiles(const std::filesystem::path& root)
{
    using namespace MiniCAD;
    double expected[4][4]{};
    std::ifstream centers(root / "spatial-navigation-center.txt");
    for (auto& row : expected)
        for (double& value : row) centers >> value;
    if (!centers) throw std::runtime_error("Spatial navigation centers missing");
    auto load = [](Document& document, const std::filesystem::path& path)
    {
        const auto utf8 = path.u8string();
        if (!document.LoadFromFile(std::string(utf8.begin(), utf8.end())))
            throw std::runtime_error("Spatial navigation output could not be read");
    };
    for (const wchar_t* extension : {L".dwg", L".dxf"})
    {
        Document document;
        load(document, root / (std::wstring(L"非俯视导航") + extension));
        int count = 0;
        const auto& scene = document.GetScene();
        scene.ForEachObject([&](const Object& object)
        {
            const auto* viewport = dynamic_cast<const ViewportEntity*>(&object);
            if (!viewport || std::abs(viewport->GetWidth() - 300) > 1e-7) return;
            const int layoutIndex = scene.FindLayout(viewport->GetAttr().LayoutKey);
            if (layoutIndex < 0 || scene.Layouts()[layoutIndex].Name != "Layout1") return;
            const int index = static_cast<int>(std::round((viewport->GetCenter().x - 150) / 300));
            if (index < 0 || index >= 4) throw std::runtime_error("Spatial navigation frame index changed");
            const auto center = viewport->GetViewCenter();
            if (std::hypot(center.x - expected[index][0], center.y - expected[index][1], center.z - expected[index][2]) > 1e-6 ||
                std::abs(1 / viewport->ModelToPaperScale() - expected[index][3]) > 1e-7 || !viewport->IsLocked())
                throw std::runtime_error("Spatial navigation save lost XYZ center, scale or lock at " + std::to_string(index) +
                    " actual " + std::to_string(center.x) + "," + std::to_string(center.y) + "," + std::to_string(center.z) +
                    " ratio " + std::to_string(1 / viewport->ModelToPaperScale()) + " lock " + std::to_string(viewport->IsLocked()));
            ++count;
        });
        if (count != 4) throw std::runtime_error("Spatial navigation saved count changed");
    }
    Document perspective;
    load(perspective, root / L"透视视口保护.dwg");
    Document restored;
    if (!restored.LoadFromString(perspective.SaveToString()))
        throw std::runtime_error("Perspective viewport internal roundtrip failed");
    int count = 0;
    restored.GetScene().ForEachObject([&](const Object& object)
    {
        const auto* viewport = dynamic_cast<const ViewportEntity*>(&object);
        if (viewport && viewport->IsPerspective()) ++count;
    });
    if (count != 1) throw std::runtime_error("Perspective viewport flag lost");

    for (const wchar_t* extension : {L".dwg", L".dxf"})
    {
        Document edited;
        load(edited, root / (std::wstring(L"透视视口移动复制") + extension));
        const auto& scene = edited.GetScene();
        const ViewportEntity* view = nullptr;
        const LineEntity* moved = nullptr;
        std::vector<Math::Point3> points;
        int modelLines = 0;
        scene.ForEachObject([&](const Object& object)
        {
            if (const auto* viewport = dynamic_cast<const ViewportEntity*>(&object); viewport && viewport->IsPerspective())
                view = viewport;
            if (const auto* line = dynamic_cast<const LineEntity*>(&object); line && !line->GetAttr().InPaperSpace)
            {
                ++modelLines;
                if (std::abs(line->GetLine().End.z - 550) < 1e-7) moved = line;
            }
            if (const auto* point = dynamic_cast<const PointEntity*>(&object); point && !point->GetAttr().InPaperSpace)
                points.push_back(point->GetPoint().Position);
        });
        if (!view || !view->IsLocked() || view->GetCameraOptics().Lens != 42 ||
            view->GetCameraOptics().Front != 600 || view->GetCameraOptics().Back != -400 ||
            (view->GetViewDirection() - Math::Point3{0,0,1000}).Length() > 1e-7)
            throw std::runtime_error("Moving through the viewport changed its camera or lock");
        if (!moved || modelLines != 5 || points.size() != 3)
            throw std::runtime_error("Perspective move and copy did not preserve model space or object counts");
        const auto [right, up] = view->ViewAxes();
        const auto normal = Math::Cross(right, up);
        const auto center = view->GetViewCenter();
        const double distance = 20 * 700 / std::hypot(300., 200.);
        const auto shiftedStart = center + right * (-120 + distance) + up * (50 + distance) + normal * 300;
        const auto& line = moved->GetLine();
        if ((line.Start - shiftedStart).Length() > 3 || std::abs(line.Start.z - 600) > 1e-7 ||
            (line.End - line.Start - right * 240 + normal * 50).Length() > 1e-7 || moved->GetAttr().Color.Aci != 1)
            throw std::runtime_error("Perspective move did not keep the line elevation, length or color");
        const auto source = center - up * 80 - normal * 150;
        for (const auto& offset : {Math::Vec3{}, {70,25,50}, {-50,80,-25}})
            if (std::none_of(points.begin(), points.end(), [&](const auto& point) { return (point - (source + offset)).Length() < 1e-7; }))
                throw std::runtime_error("Perspective point copy lost the source or a relative XYZ destination");
    }
}
