#pragma once
#include "Document/CommandStack/ICommand.h"
#include "Scene/Scene.h"

struct CadDrawingSettings
{
    MiniCAD::LineTypeTable Types;
    uint32_t CurrentType;
    double Scale, PointSize;
    int PointMode;
    bool PaperScale;
    explicit CadDrawingSettings(const MiniCAD::Scene& scene)
        : Types(scene.GetLineTypeTable()), CurrentType(scene.GetCurrentLineType()), Scale(scene.GetLineTypeScale()),
          PointSize(scene.GetPointDisplaySize()), PointMode(scene.GetPointDisplayMode()), PaperScale(scene.GetPaperSpaceLineTypeScaling()) {}
    void Apply(MiniCAD::Scene& scene) const
    {
        scene.GetLineTypeTable() = Types; scene.SetCurrentLineType(CurrentType);
        scene.SetLineTypeScale(Scale); scene.SetPaperSpaceLineTypeScaling(PaperScale);
        scene.SetPointDisplayMode(PointMode); scene.SetPointDisplaySize(PointSize); scene.MarkDirty();
    }
};
class CadDrawingSettingsCommand : public MiniCAD::ICommand
{
public:
    CadDrawingSettingsCommand(CadDrawingSettings before, CadDrawingSettings after) : m_before(std::move(before)), m_after(std::move(after)) {}
    bool Execute(MiniCAD::Scene& scene) override { m_after.Apply(scene); return true; }
    void Undo(MiniCAD::Scene& scene) override { m_before.Apply(scene); }
    std::string GetName() const override { return "绘图样式设置"; }
private:
    CadDrawingSettings m_before, m_after;
};
