#pragma once
#include "Database/CadDatabase.h"
#include "Dwg/Read/DwgReader.h"
#include "Dxf/Read/DxfReader.h"
#include "Render/ImageDraw.hpp"
#include "Document/WipeClip.hpp"
#include "Document/Document.h"
#include "DetailedClippingCheck.h"
#include <cmath>
#include <filesystem>
#include <fstream>
#include <stdexcept>

inline void CheckLayoutFiles(const std::filesystem::path& root)
{
    CheckDetailedClipping();
    auto check = [](bool condition, const char* message) { if (!condition) throw std::runtime_error(message); };
    {
        const MiniCAD::WipeClip::Poly polygon(std::vector<MiniCAD::Math::Point3>{{0,0,0},{300,0,0},{300,200,0},{150,200,0},{150,100,0},{0,100,0}});
        std::vector<MiniCAD::Vertex_P3_C4> triangles(6);
        const MiniCAD::Math::Float3 points[]={{0,0,0},{300,0,0},{300,200,0},{0,0,0},{300,200,0},{0,200,0}};
        for (int i=0;i<6;++i) triangles[i].pos=points[i];
        MiniCAD::WipeClip::ClipFills(triangles,polygon,5,0,true);
        double area=0;
        for (size_t i=0;i+2<triangles.size();i+=3)
        {
            const auto a=triangles[i].pos,b=triangles[i+1].pos,c=triangles[i+2].pos;
            area+=std::abs((b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x))/2;
        }
        check(std::abs(area-45000)<.1,"Concave viewport fill clipping area changed");
        std::vector<MiniCAD::Vertex_P3_C4> line(2); line[0].pos={-100,150,0}; line[1].pos={400,150,0};
        MiniCAD::WipeClip::ClipLines(line,polygon,0,true);
        check(line.size()==2 && std::abs(line[0].pos.x-150)<.001 && std::abs(line[1].pos.x-300)<.001,"Concave viewport line clipping failed");
    }
    auto read = [&](const std::filesystem::path& path)
    {
        std::ifstream file(path, std::ios::binary);
        const std::vector<std::uint8_t> bytes{std::istreambuf_iterator<char>(file), {}};
        auto database = path.extension() == L".dxf" ? MiniDWG::ReadDxf(bytes) : MiniDWG::ReadDwg(bytes);
        check(database != nullptr, "Layout output could not be read");
        return database;
    };
    auto find = [&](const MiniDWG::CadDatabase& database, const std::string& name)
    {
        const MiniDWG::Layout* result = nullptr;
        for (const auto& [handle, object] : database.Objects())
            if (const auto* layout = dynamic_cast<const MiniDWG::Layout*>(object.get()); layout && layout->Name == name)
            { check(result == nullptr, "Duplicate layout object found"); result = layout; }
        check(result != nullptr, "Named layout was lost"); return result;
    };
    for (const wchar_t* extension : {L".dwg", L".dxf"})
    {
        auto metadata = read(root / (std::wstring(L"layout-metadata") + extension));
        const auto* inchPage = find(*metadata, "英制页面");
        check(inchPage->PaperUnits == MiniDWG::PlotPaperUnits::Inches && std::abs(inchPage->PaperWidth - 609.6) < .001,
              "Inch layout physical paper size changed during saving");
        for (const char* name : {"改名页面", "复制页面"})
        {
            const auto* page = find(*metadata, name);
            check(page->PageName == "测试页面设置" && page->SystemPrinterName == "DWG To PDF.pc3" && page->StyleSheet == "monochrome.ctb", "Page setup name/printer/style was lost");
            check(page->PaperWidth == 594 && page->PaperHeight == 420 && static_cast<int>(page->PaperRotation) == 1, "Original physical paper size/rotation changed");
            check(page->DenominatorScale == 2 && page->PlotOriginX == 7 && page->PlotOriginY == 9, "Original plot scale/origin changed");
        }
        auto copied = read(root / (std::wstring(L"布局修改复制") + extension));
        const auto* first = find(*copied, "平面布置"); const auto* second = find(*copied, "复制布置");
        check(first->AssociatedBlockHandle != second->AssociatedBlockHandle && second->TabOrder == 1, "Copied layout identity/order changed");
        for (const auto* layout : {first, second})
        {
            const auto* paper = copied->FindAs<MiniDWG::BlockRecord>(layout->AssociatedBlockHandle);
            int viewports = 0;
            for (auto handle : paper->Entities)
                if (const auto* viewport = copied->FindAs<MiniDWG::Viewport>(handle); viewport && viewport->ViewHeight > 0) ++viewports;
            check(viewports == 2, "Copied layout has the wrong viewport count");
        }
        auto removed = read(root / (std::wstring(L"布局删除") + extension));
        const auto layoutCount = [](const MiniDWG::CadDatabase& database)
        {
            int count = 0;
            for (const auto& [handle, object] : database.Objects()) if (dynamic_cast<const MiniDWG::Layout*>(object.get())) ++count;
            return count;
        };
        check(layoutCount(*removed) == layoutCount(*copied) - 1, "Deleting a layout resurrected an orphan page");
        const auto rotatedPath = root / (std::wstring(L"旋转椭圆视口") + extension);
        for (bool duplicate : {false, true})
        {
            auto rotated = read(duplicate ? root / (std::wstring(L"旋转椭圆布局复制") + extension) : rotatedPath);
            MiniDWG::Handle firstBoundary{};
            for (const char* name : {"裁剪平面", "旋转椭圆副本"})
            {
                if (firstBoundary && !duplicate) break;
                const auto* layout = find(*rotated, name);
                const auto* paper = rotated->FindAs<MiniDWG::BlockRecord>(layout->AssociatedBlockHandle);
                const MiniDWG::Viewport* viewport = nullptr;
                for (auto handle : paper->Entities)
                    if (const auto* view = rotated->FindAs<MiniDWG::Viewport>(handle); view && view->BoundaryHandle) viewport = view;
                check(viewport != nullptr, "Missing rotated ellipse viewport");
                const auto* ellipse = rotated->FindAs<MiniDWG::Ellipse>(viewport->BoundaryHandle);
                check(ellipse && ellipse->OwnerHandle == paper->ObjectHandle && ellipse->ObjectHandle != firstBoundary,
                    "Rotated clip was saved as a circle or shares a boundary after layout duplication");
                check(std::abs(ellipse->Center.X - 170) < 1e-8 && std::abs(ellipse->Center.Y - 110) < 1e-8 &&
                    std::abs(ellipse->MajorAxisEndPoint.X - 100) < 1e-8 && std::abs(ellipse->MajorAxisEndPoint.Y - 100) < 1e-8 &&
                    std::abs(ellipse->RadiusRatio - .4) < 1e-8 && std::abs(viewport->ViewHeight / viewport->Height - 50) < 1e-8,
                    "Rotated ellipse center, axes or viewport scale were lost");
                firstBoundary = ellipse->ObjectHandle;
            }
        }
        MiniCAD::Document source;
        const auto utf8 = rotatedPath.u8string();
        check(source.LoadFromFile(std::string(utf8.begin(), utf8.end())), "Rotated viewport could not be opened");
        MiniCAD::Document restored;
        check(restored.LoadFromString(source.SaveToString()), "Rotated viewport could not be serialized");
        int ellipseCount = 0;
        restored.GetScene().ForEachObject([&](const MiniCAD::Object& object)
        {
            const auto* viewport = dynamic_cast<const MiniCAD::ViewportEntity*>(&object);
            if (!viewport || !viewport->ClipEllipse()) return;
            ++ellipseCount;
            check(viewport->ContainsPaper({245,185,0}) && !viewport->ContainsPaper({225,55,0}),
                "Rotated viewport hit testing did not use the ellipse orientation");
        });
        check(ellipseCount == 1, "Rotated viewport lost its analytic clip after reloading");

        const auto polylinePath = root / (std::wstring(L"圆弧多段线视口") + extension);
        auto polylineCopy = read(root / (std::wstring(L"圆弧多段线布局复制") + extension));
        MiniDWG::Handle originalArcBoundary{};
        for (const char* name : {"裁剪平面", "圆弧轮廓副本"})
        {
            const auto* layout = find(*polylineCopy, name);
            const auto* paper = polylineCopy->FindAs<MiniDWG::BlockRecord>(layout->AssociatedBlockHandle);
            const MiniDWG::Viewport* viewport = nullptr;
            int curves = 0;
            for (auto handle : paper->Entities)
            {
                if (const auto* view = polylineCopy->FindAs<MiniDWG::Viewport>(handle); view && view->BoundaryHandle)
                    viewport = view;
                if (polylineCopy->FindAs<MiniDWG::LwPolyline>(handle)) ++curves;
            }
            check(viewport && curves == 2, "The layout should contain one source and one clipping polyline");
            const auto* boundary = polylineCopy->FindAs<MiniDWG::LwPolyline>(viewport->BoundaryHandle);
            check(boundary && boundary->OwnerHandle == paper->ObjectHandle && boundary->ObjectHandle != originalArcBoundary &&
                boundary->Vertices.size() == 4 && std::abs(boundary->Vertices[1].Bulge - 1) < 1e-8,
                "The viewport copy did not retain an independent native arc boundary");
            originalArcBoundary = boundary->ObjectHandle;
        }
        MiniCAD::Document arcSource;
        const auto arcUtf8 = polylinePath.u8string();
        check(arcSource.LoadFromFile(std::string(arcUtf8.begin(), arcUtf8.end())), "Arc viewport could not be opened");
        MiniCAD::Document arcRestored;
        check(arcRestored.LoadFromString(arcSource.SaveToString()), "Arc viewport could not be serialized");
        int arcCount = 0;
        arcRestored.GetScene().ForEachObject([&](const MiniCAD::Object& object)
        {
            const auto* viewport = dynamic_cast<const MiniCAD::ViewportEntity*>(&object);
            if (!viewport || !viewport->ClipPolylineDefinition()) return;
            ++arcCount;
            const auto curve = viewport->PaperClipPolyline();
            check(curve && curve->Points.size() == 5 && std::abs(curve->SegBulge(1)-1) < 1e-8 &&
                viewport->ContainsPaper({245,110,0}) && !viewport->ContainsPaper({245,150,0}),
                "Arc viewport lost clipping or its original circular arc");
        });
        check(arcCount == 1, "Arc viewport lost its analytic clip after reloading");

        const auto splinePath = root / (std::wstring(L"样条视口") + extension);
        auto splineCopy = read(root / (std::wstring(L"样条布局复制") + extension));
        MiniDWG::Handle originalSplineBoundary{};
        for (const char* name : {"裁剪平面", "样条轮廓副本"})
        {
            const auto* layout = find(*splineCopy, name);
            const auto* paper = splineCopy->FindAs<MiniDWG::BlockRecord>(layout->AssociatedBlockHandle);
            const MiniDWG::Viewport* viewport = nullptr;
            int splines = 0;
            for (auto handle : paper->Entities)
            {
                if (const auto* view = splineCopy->FindAs<MiniDWG::Viewport>(handle); view && view->BoundaryHandle)
                    viewport = view;
                if (splineCopy->FindAs<MiniDWG::Spline>(handle)) ++splines;
            }
            check(viewport && splines == 2 && std::abs(viewport->ViewHeight / viewport->Height - 50) < 1e-8,
                "The layout should contain the source spline, its clip and the original view scale");
            const auto* boundary = splineCopy->FindAs<MiniDWG::Spline>(viewport->BoundaryHandle);
            check(boundary && boundary->OwnerHandle == paper->ObjectHandle && boundary->ObjectHandle != originalSplineBoundary &&
                (static_cast<int>(boundary->Flags) & 1) && boundary->Degree == 3 &&
                boundary->ControlPoints.size() == 13 && boundary->Knots.size() == 17,
                "The layout copy lost the closed native spline boundary");
            originalSplineBoundary = boundary->ObjectHandle;
        }
        MiniCAD::Document splineSource;
        const auto splineUtf8 = splinePath.u8string();
        check(splineSource.LoadFromFile(std::string(splineUtf8.begin(), splineUtf8.end())), "Spline viewport could not be opened");
        MiniCAD::Document splineRestored;
        check(splineRestored.LoadFromString(splineSource.SaveToString()), "Spline viewport could not be serialized");
        const MiniCAD::Spline fitted({{70,110,0},{140,50,0},{225,110,0},{140,180,0}}, MiniCAD::SplineBoundary::Closed);
        int splineCount = 0;
        splineRestored.GetScene().ForEachObject([&](const MiniCAD::Object& object)
        {
            const auto* viewport = dynamic_cast<const MiniCAD::ViewportEntity*>(&object);
            if (!viewport || !viewport->ClipSplineDefinition()) return;
            ++splineCount;
            check(viewport->ContainsPaper({140,110,0}) && !viewport->ContainsPaper({75,170,0}),
                "Spline clipping did not survive reloading");
            const auto original = viewport->PaperClipSpline();
            check(original.has_value(), "Native spline boundary is missing after reopening");
            for (size_t i = 0; i < fitted.Segments.size(); ++i)
                for (double t : {.25, .5, .75})
                    check((original->EvaluateNURBS(double(i) + t) - fitted.Segments[i].Evaluate(t)).Length() < 1e-8,
                        "Converting fitted points to native spline control points changed the curve");
            auto clone = viewport->Clone(viewport->GetID());
            auto& resized = static_cast<MiniCAD::ViewportEntity&>(*clone);
            resized.SetSize(2 * viewport->GetWidth(), viewport->GetHeight());
            const auto scaled = resized.PaperClipSpline();
            const auto origin = viewport->GetCenter();
            check(original && scaled && scaled->Knots == original->Knots &&
                std::abs(scaled->ControlPoints.front().x - (2 * original->ControlPoints.front().x - origin.x)) < 1e-8 &&
                std::abs(scaled->ControlPoints.front().y - original->ControlPoints.front().y) < 1e-8,
                "Resizing the viewport did not preserve the spline under an affine scale");
        });
        check(splineCount == 1, "Viewport lost its analytic spline after reloading");
    }
}
