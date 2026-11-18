#pragma once
#include "Document/CommandStack/ICommand.h"
#include "Serialization/JsonSerializer.h"
#include "Scene/Scene.h"
#include "Core/Entity/InsertEntity.hpp"
#include "Core/Entity/TextEntity.hpp"
#include "Core/Entity/AttribEntity.hpp"
#include "Core/Entity/MLeaderEntity.hpp"
#include "Core/Entity/DimensionEntity.hpp"
#include "Core/Entity/LeaderEntity.hpp"
#include "Core/Entity/ToleranceEntity.hpp"
#include "Core/Entity/MLineEntity.hpp"
#include "Core/Entity/ViewportEntity.hpp"
#include <map>
#include <stdexcept>

namespace MiniCAD::ClipboardTransfer
{
    // Dependency snapshots exclude model-space entities. Table IDs and existing
    // definitions remain stable when a paste is cancelled, undone or redone.
    inline std::string SaveTables(const Scene& scene)
    {
        JsonSerializer writer;
        if (writer.BeginObject("layers")) { scene.GetLayerManager().Serialize(writer); writer.EndObject(); }
        if (writer.BeginObject("text")) { scene.GetTextStyleTable().Serialize(writer); writer.EndObject(); }
        if (writer.BeginObject("types")) { scene.GetLineTypeTable().Serialize(writer); writer.EndObject(); }
        if (writer.BeginObject("mline")) { scene.GetMLineStyleTable().Serialize(writer); writer.EndObject(); }
        if (writer.BeginObject("blocks")) { scene.GetBlockTable().Serialize(writer); writer.EndObject(); }
        return writer.Dump();
    }
    inline void RestoreTables(Scene& scene, const std::string& text)
    {
        JsonSerializer reader;
        if (!reader.Parse(text)) throw std::runtime_error("Clipboard dependency snapshot is invalid");
        if (reader.BeginObject("layers")) { scene.GetLayerManager().Deserialize(reader); reader.EndObject(); }
        if (reader.BeginObject("text")) { scene.GetTextStyleTable().Deserialize(reader); reader.EndObject(); }
        if (reader.BeginObject("types")) { scene.GetLineTypeTable().Deserialize(reader); reader.EndObject(); }
        if (reader.BeginObject("mline")) { scene.GetMLineStyleTable().Deserialize(reader); reader.EndObject(); }
        if (reader.BeginObject("blocks")) { scene.GetBlockTable().Deserialize(reader); reader.EndObject(); }
        scene.ResolveAllInserts(); scene.MarkDirty();
    }

    class Merger
    {
    public:
        Merger(const Scene& source, Scene& target) : m_source(source), m_target(target) {}
        void Remap(Entity& entity)
        {
            auto attr = entity.GetAttr();
            attr.LayerId = Layer(attr.LayerId); attr.LineType = LineType(attr.LineType);
            entity.SetAttr(attr);
            if (auto* viewport = dynamic_cast<ViewportEntity*>(&entity))
            {
                std::vector<LayerID> frozen;
                for (auto id : viewport->GetFrozenLayers()) frozen.push_back(Layer(id));
                viewport->SetFrozenLayers(std::move(frozen));
            }
            if (auto* text = dynamic_cast<TextEntity*>(&entity)) text->SetStyleId(Text(text->GetStyleId()));
            if (auto* attribute = dynamic_cast<AttribEntity*>(&entity)) attribute->SetStyleId(Text(attribute->GetStyleId()));
            if (auto* definition = dynamic_cast<AttDefEntity*>(&entity)) definition->SetStyleId(Text(definition->GetStyleId()));
            if (auto* dimension = dynamic_cast<DimensionEntity*>(&entity))
            { auto style = dimension->GetStyle(); style.TextStyle = Text(style.TextStyle); dimension->SetStyle(style); }
            if (auto* leader = dynamic_cast<LeaderEntity*>(&entity)) leader->GetStyle().TextStyle = Text(leader->GetStyle().TextStyle);
            if (auto* leader = dynamic_cast<MLeaderEntity*>(&entity))
            {
                leader->GetStyle().TextStyle = Text(leader->GetStyle().TextStyle);
                if (leader->GetBlockId()) leader->SetBlockId(Block(leader->GetBlockId()));
            }
            if (auto* tolerance = dynamic_cast<ToleranceEntity*>(&entity)) tolerance->GetStyle().TextStyle = Text(tolerance->GetStyle().TextStyle);
            if (auto* mline = dynamic_cast<MLineEntity*>(&entity)) m_target.GetMLineStyleTable().Add(mline->GetMLineStyle());
            if (auto* insert = dynamic_cast<InsertEntity*>(&entity))
            {
                const auto block = Block(insert->GetBlockId());
                const auto* definition = m_target.GetBlockTable().Find(block);
                insert->SetReference(block, definition ? definition->GetName() : insert->GetBlockName());
                for (const auto& attribute : insert->GetAttribs()) if (attribute) Remap(*attribute);
            }
            if (auto* block = dynamic_cast<BlockEntity*>(&entity)) for (const auto& child : block->GetEntities()) if (child) Remap(*child);
        }
    private:
        uint32_t Text(uint32_t id)
        {
            if (auto found = m_text.find(id); found != m_text.end()) return found->second;
            auto record = m_source.GetTextStyleTable().Resolve(id);
            auto target = m_target.GetTextStyleTable().FindByName(record.Name);
            if (target == TextStyleTable::InvalidID) target = m_target.GetTextStyleTable().Add(record);
            return m_text[id] = target;
        }
        uint32_t LineType(uint32_t id)
        {
            if (auto found = m_types.find(id); found != m_types.end()) return found->second;
            const auto* source = m_source.GetLineTypeTable().Find(id);
            if (!source) return LineTypeTable::ByLayerID;
            auto record = *source;
            for (auto& symbol : record.Symbols) symbol.StyleId = Text(symbol.StyleId);
            return m_types[id] = m_target.GetLineTypeTable().Add(std::move(record));
        }
        uint32_t Layer(uint32_t id)
        {
            if (auto found = m_layers.find(id); found != m_layers.end()) return found->second;
            const auto* source = m_source.GetLayerManager().GetLayer(id);
            if (!source || id == 0) return 0;
            auto& layers = m_target.GetLayerManager();
            for (auto candidate : layers.GetAllLayerIDs())
                if (_stricmp(layers.GetLayer(candidate)->GetName().c_str(), source->GetName().c_str()) == 0) return m_layers[id] = candidate;
            const auto target = layers.AddLayer(source->GetName());
            auto* layer = layers.GetLayer(target);
            layer->SetColor(source->GetColor()); layer->SetVisible(source->IsVisible()); layer->SetLocked(source->IsLocked());
            layer->SetLineType(LineType(source->GetLineType())); layer->SetLineweight(source->GetLineweight());
            return m_layers[id] = target;
        }
        uint32_t Block(uint32_t id)
        {
            if (auto found = m_blocks.find(id); found != m_blocks.end()) return found->second;
            const auto* source = m_source.GetBlockTable().Find(id);
            if (!source) return BlockTable::ModelSpaceID;
            auto& blocks = m_target.GetBlockTable();
            auto target = blocks.FindByName(source->GetName());
            if (target != BlockTable::InvalidID) return m_blocks[id] = target;
            target = blocks.Create(source->GetName(), source->GetBasePoint());
            m_blocks[id] = target; // Register before descending through nested definitions.
            auto* block = blocks.Find(target);
            block->SetFlags(source->GetFlags()); block->SetDescription(source->GetDescription());
            block->SetXRefPath(source->GetXRefPath()); block->SetXRefUnloaded(source->IsXRefUnloaded());
            for (const auto& child : source->GetEntities())
            {
                if (!child) continue;
                auto copy = child->Clone(m_target.NextObjectID()); Remap(*copy); block->AddEntity(std::move(copy));
            }
            return target;
        }
        const Scene& m_source;
        Scene& m_target;
        std::map<uint32_t, uint32_t> m_layers, m_types, m_text, m_blocks;
    };

    class PasteCommand : public ICommand
    {
    public:
        PasteCommand(std::shared_ptr<Scene> dependencies, std::vector<std::unique_ptr<Object>> entities)
            : m_source(std::move(dependencies)), m_entities(std::move(entities)) {}
        bool Execute(Scene& scene) override
        {
            if (m_entities.empty()) return false;
            if (m_after.empty())
            {
                m_before = SaveTables(scene);
                try
                {
                    Merger merger(*m_source, scene);
                    for (auto& object : m_entities)
                    {
                        auto& entity = static_cast<Entity&>(*object); merger.Remap(entity);
                        auto attr = entity.GetAttr(); attr.InPaperSpace = scene.CurrentSpaceIsPaper(); attr.LayoutKey = scene.CurrentLayoutKey(); entity.SetAttr(attr);
                    }
                    m_after = SaveTables(scene);
                }
                catch (...) { RestoreTables(scene, m_before); throw; }
            }
            else RestoreTables(scene, m_after);
            for (const auto& object : m_entities) scene.AddEntity(static_cast<const Entity&>(*object).Clone(object->GetID()));
            scene.ResolveAllInserts(); scene.MarkDirty(); return true;
        }
        void Undo(Scene& scene) override
        {
            for (const auto& object : m_entities) scene.RemoveEntity(object->GetID());
            RestoreTables(scene, m_before);
        }
        std::string GetName() const override { return "粘贴"; }
    private:
        std::shared_ptr<Scene> m_source;
        std::vector<std::unique_ptr<Object>> m_entities;
        std::string m_before, m_after;
    };
}
