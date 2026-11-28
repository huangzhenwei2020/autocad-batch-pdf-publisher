#include "Import/CadExchange.h"
#include "Scene/Scene.h"
#include "Core/Entity/LineEntity.hpp"
#include "Core/Entity/InsertEntity.hpp"
#include "Core/Entity/ViewportEntity.hpp"
#include "Core/Entity/ImageEntity.hpp"
#include "Core/Entity/TextEntity.hpp"
#include "Dwg/Read/DwgReader.h"
#include "Dwg/Write/DwgWriter.h"
#include "LayoutFileCheck.h"
#include <filesystem>
#include <fstream>
#include <stdexcept>
#include <iostream>
#include "SpatialViewportCheck.h"
#include "WorkingPlaneCheck.h"
#include "SpatialCurveCheck.h"
#include "WorkingPolylineCheck.h"
#include "WorkingEllipseCheck.h"
#include "WorkingHatchCheck.h"

static int RunFixtures(int count, wchar_t** arguments)
{
    if (count == 3 && std::wstring(arguments[1]) == L"--verify-layouts")
    {
        CheckLayoutFiles(arguments[2]);
        CheckSpatialNavigationFiles(arguments[2]);
        CheckPerspectiveCurveFiles(arguments[2]);
        CheckWorkingPolylineFiles(arguments[2]);
        CheckWorkingEllipseFiles(arguments[2]);
        return 0;
    }
    if (count != 2) return 2;
    const std::filesystem::path root(arguments[1]);
    std::filesystem::create_directories(root);
    WriteSpatialViewportFixture(root);
    WriteSpatialInlineClipFixture(root);
    WritePerspectiveProjectionFixture(root);
    WritePerspectiveProjectionFixture(root, true);
    CheckWorkingPlane();
    CheckSpatialCurves();
    CheckWorkingPolyline();
    CheckWorkingEllipse();
    CheckWorkingHatch();
    auto write = [&](const wchar_t* name, const MiniCAD::Scene& scene)
    {
        const auto data = MiniCAD::ExportCad(scene, MiniCAD::CadFileKind::Dwg, MiniCAD::CadSaveVersion::R2018);
        if (data.empty()) throw std::runtime_error("Fixture export failed");
        std::ofstream stream(root / name, std::ios::binary);
        stream.write(reinterpret_cast<const char*>(data.data()), data.size());
        if (!stream.good()) throw std::runtime_error("Fixture write failed");
    };
    MiniCAD::Scene reference;
    reference.AddEntity(std::make_unique<MiniCAD::LineEntity>(reference.NextObjectID(), MiniCAD::Math::Point3{}, MiniCAD::Math::Point3{1000, 0, 0}));
    write(L"separate-xref.dwg", reference);
    MiniCAD::Scene host;
    const auto id = host.GetBlockTable().Create("测试外参");
    auto* block = host.GetBlockTable().Find(id);
    block->SetFlags(MiniCAD::BlockEntity::XRef); block->SetXRefPath("Z:/WanluoFixtureMissing/共享轴网.dwg");
    host.AddEntity(std::make_unique<MiniCAD::InsertEntity>(host.NextObjectID(), id, block->GetName(), MiniCAD::Math::Point3{100, 200, 0}));
    write(L"host-xref.dwg", host);
    const auto mediaPath = (root / L"detailed-checker.png").u8string();
    const std::string mediaFile(mediaPath.begin(), mediaPath.end());
    auto mediaFixture = [&](const wchar_t* name, int kind)
    {
        MiniCAD::Scene scene;
        MiniCAD::DrawingLayout paper;
        paper.Name = "媒体裁剪";
        paper.Model = false;
        scene.SetLayouts({MiniCAD::DrawingLayout{}, paper});
        if (kind == 2)
            scene.AddEntity(std::make_unique<MiniCAD::TextEntity>(scene.NextObjectID(), MiniCAD::Math::Point3{120, 135, 0}, "MMMM", 40.f));
        else
        {
            const double x0 = kind == 0 ? -100 : 50, x1 = kind == 0 ? 400 : 170;
            const double y0 = kind == 0 ? -100 : 120, y1 = kind == 0 ? 300 : 180;
            auto image = std::make_unique<MiniCAD::ImageEntity>(scene.NextObjectID(), mediaFile,
                MiniCAD::Math::Point3{x0, y0, 0}, MiniCAD::Math::Point3{x1, y0, 0},
                MiniCAD::Math::Point3{x1, y1, 0}, MiniCAD::Math::Point3{x0, y1, 0});
            image->SetShowFrame(false);
            scene.AddEntity(std::move(image));
        }
        auto viewport = std::make_unique<MiniCAD::ViewportEntity>(scene.NextObjectID(), MiniCAD::Math::Point3{150, 100, 0}, 300, 200);
        viewport->SetViewCenter({150, 100, 0});
        viewport->SetViewHeight(200);
        viewport->SetViewportId(2);
        viewport->SetLocked(false);
        viewport->SetClipCoordinates({{-.5, -.5, 0}, {.5, -.5, 0}, {.5, .5, 0}, {0, .5, 0}, {0, 0, 0}, {-.5, 0, 0}});
        viewport->GetAttr().InPaperSpace = true;
        viewport->GetAttr().LayoutKey = scene.Layouts()[1].OwnerKey;
        scene.AddEntity(std::move(viewport));
        write(name, scene);
    };
    mediaFixture(L"viewport-media.dwg", 0);
    mediaFixture(L"viewport-media-outside.dwg", 1);
    mediaFixture(L"viewport-media-text.dwg", 2);
    MiniCAD::Scene paperScene;
    MiniCAD::DrawingLayout layout; layout.Name = "原始页面"; layout.Model = false;
    MiniCAD::DrawingLayout inches; inches.Name = "英制页面"; inches.Model = false;
    paperScene.SetLayouts({MiniCAD::DrawingLayout{}, layout, inches});
    paperScene.AddEntity(std::make_unique<MiniCAD::LineEntity>(paperScene.NextObjectID(), MiniCAD::Math::Point3{}, MiniCAD::Math::Point3{10000, 6000, 0}));
    auto viewport = std::make_unique<MiniCAD::ViewportEntity>(paperScene.NextObjectID(), MiniCAD::Math::Point3{210, 148, 0}, 300, 180);
    viewport->SetViewCenter({5000, 3000, 0}); viewport->SetViewHeight(9000); viewport->SetViewportId(2);
    viewport->GetAttr().InPaperSpace = true; viewport->GetAttr().LayoutKey = paperScene.Layouts()[1].OwnerKey;
    paperScene.AddEntity(std::move(viewport));
    const auto paperBytes = MiniCAD::ExportCad(paperScene, MiniCAD::CadFileKind::Dwg, MiniCAD::CadSaveVersion::R2018);
    auto database = MiniDWG::ReadDwg(paperBytes);
    if (!database) throw std::runtime_error("Layout fixture could not be read");
    for (const auto& [handle, object] : database->Objects())
        if (auto* page = dynamic_cast<MiniDWG::Layout*>(object.get()); page && page->Name == "原始页面")
        {
            page->PageName = "测试页面设置"; page->SystemPrinterName = "DWG To PDF.pc3";
            page->StyleSheet = "monochrome.ctb"; page->PaperWidth = 594; page->PaperHeight = 420;
            page->PaperRotation = static_cast<MiniDWG::PlotRotation>(1);
            page->NumeratorScale = 1; page->DenominatorScale = 2;
            page->PlotOriginX = 7; page->PlotOriginY = 9;
        }
        else if (auto* page = dynamic_cast<MiniDWG::Layout*>(object.get()); page && page->Name == "英制页面")
        {
            page->PaperWidth = 609.6; page->PaperHeight = 457.2;
            page->PaperRotation = static_cast<MiniDWG::PlotRotation>(0);
            page->PaperUnits = MiniDWG::PlotPaperUnits::Inches;
        }
    const auto configured = MiniDWG::WriteDwg(*database);
    std::ofstream paperFile(root / L"layout-source.dwg", std::ios::binary);
    paperFile.write(reinterpret_cast<const char*>(configured.data()), configured.size());
    if (!paperFile.good() || configured.empty()) throw std::runtime_error("Layout fixture export failed");
    return 0;
}

int wmain(int count, wchar_t** arguments)
{
    try { return RunFixtures(count, arguments); }
    catch (const std::exception& error) { std::cerr << error.what() << std::endl; return 1; }
}
