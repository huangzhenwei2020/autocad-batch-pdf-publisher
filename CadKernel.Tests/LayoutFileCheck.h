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
    }
}
