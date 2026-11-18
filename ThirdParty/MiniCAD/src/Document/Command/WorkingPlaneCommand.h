#pragma once
#include "Document/CommandStack/ICommand.h"
#include "Scene/Scene.h"

namespace MiniCAD
{
    class WorkingPlaneCommand final : public ICommand
    {
    public:
        WorkingPlaneCommand(const Scene& scene, const Math::WorkingPlane& after)
            : m_before(scene.GetWorkingPlane()), m_after(after), m_beforeEdited(scene.WorkingPlaneEdited()) {}
        bool Execute(Scene& scene) override { scene.SetWorkingPlane(m_after); return true; }
        void Undo(Scene& scene) override { scene.SetWorkingPlane(m_before, m_beforeEdited); }
        std::string GetName() const override { return "设置绘图工作平面"; }
    private:
        Math::WorkingPlane m_before, m_after;
        bool m_beforeEdited;
    };
}
