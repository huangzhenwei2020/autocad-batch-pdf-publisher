#pragma once
#include "Document/CommandStack/ICommand.h"
#include "Scene/Scene.h"
#include "Core/Entity/MLineEntity.hpp"
#include <map>

// Style tables are small document-owned records. Preserve IDs and current choices
// together so creating a style and selecting it form one undoable edit.
struct CadStyleState
{
    MiniCAD::TextStyleTable Text;
    MiniCAD::MLineStyleTable MLine;
    uint32_t CurrentText, CurrentMLine;

    explicit CadStyleState(const MiniCAD::Scene& scene)
        : Text(scene.GetTextStyleTable()), MLine(scene.GetMLineStyleTable()),
          CurrentText(scene.GetCurrentTextStyle()), CurrentMLine(scene.GetCurrentMLineStyle()) {}

    void Apply(MiniCAD::Scene& scene) const
    {
        std::map<std::string, std::string> names;
        for (const auto& old : scene.GetMLineStyleTable().Records())
            if (const auto* next = MLine.Find(old.Id); next && next->Name != old.Name) names[old.Name] = next->Name;
        std::function<void(MiniCAD::Object&)> rename = [&](MiniCAD::Object& object)
        {
            if (auto* mline = dynamic_cast<MiniCAD::MLineEntity*>(&object))
                if (auto found = names.find(mline->GetMLineStyle().Name); found != names.end())
                { auto style = mline->GetMLineStyle(); style.Name = found->second; mline->SetMLineStyle(std::move(style)); }
            if (auto* block = dynamic_cast<MiniCAD::BlockEntity*>(&object))
                for (const auto& child : block->GetEntities()) if (child) rename(*child);
        };
        for (auto id : scene.GetAllIDs()) if (auto* object = scene.GetEntity(id)) rename(*object);
        scene.GetBlockTable().ForEach([&](MiniCAD::BlockID id, const MiniCAD::BlockEntity&) { rename(*scene.GetBlockTable().Find(id)); });
        scene.GetTextStyleTable() = Text;
        scene.GetMLineStyleTable() = MLine;
        scene.SetCurrentTextStyle(CurrentText);
        scene.SetCurrentMLineStyle(CurrentMLine);
        scene.MarkDirty();
    }
};

class CadStyleCommand : public MiniCAD::ICommand
{
public:
    CadStyleCommand(CadStyleState before, CadStyleState after)
        : m_before(std::move(before)), m_after(std::move(after)) {}
    bool Execute(MiniCAD::Scene& scene) override { m_after.Apply(scene); return true; }
    void Undo(MiniCAD::Scene& scene) override { m_before.Apply(scene); }
    std::string GetName() const override { return "修改命名样式"; }
private:
    CadStyleState m_before, m_after;
};
