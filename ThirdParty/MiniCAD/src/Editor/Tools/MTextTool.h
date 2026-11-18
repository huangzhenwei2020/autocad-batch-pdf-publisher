#pragma once
#include "Editor/Tools/ITool.h"
#include "Editor/EditorContext.h"
#include "Core/Math/Point3.hpp"
#include <functional>
#include "Core/Log.h"

namespace MiniCAD
{
    class MTextTool : public ITool
    {
    public:
        bool SupportsWorkingPlane() const override { return true; }
        MTextTool()
        {
            LOG_DEBUG("[MTextTool] click insertion point | ESC exits");
        }

        ~MTextTool() { LOG_DEBUG("Exit multiline text"); }

        std::function<void(Math::Point3)> OnInsertPointPicked;

        bool OnInput(const EditorContext& ctx) override
        {
            const auto& e = ctx.event;

            if (e.IsLeftClick())
            {
                if (!e.HasSnap) return true;

                if (OnInsertPointPicked)
                    OnInsertPointPicked(e.SnapWorld);

                if (OnFinished) OnFinished();
                return true;
            }

            if (e.IsRightClick() || e.IsCancel())
            {
                ctx.overlay.Clear();
                if (OnFinished) OnFinished();
                return true;
            }

            return false;
        }

        std::string GetPrompt() const override
        {
            return Tr("指定多行文字插入点 [ESC 退出]:");
        }
    };
}
