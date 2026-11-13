#pragma once
#include "Import/CadDimensionExchange.h"
#include "Core/Entity/TableEntity.hpp"
#include "Scene/TextStyleTable.h"

namespace MiniCAD::CadTableExchange
{
    inline std::string Data(const TableEntity& table, const std::string& styleName)
    {
        std::ostringstream out; out.imbue(std::locale::classic()); out << std::setprecision(17) << "WanluoTableV1 ";
        const auto& p = table.GetPosition();
        out << p.x << ' ' << p.y << ' ' << p.z << ' ' << table.GetHeight() << ' ' << table.GetRotation() << ' ' << table.Margin()
            << ' ' << table.RowCount() << ' ' << table.ColCount() << ' ' << std::quoted(styleName) << ' ';
        for (double height : table.RowHeights()) out << height << ' ';
        for (double width : table.ColWidths()) out << width << ' ';
        for (size_t row = 0; row < table.RowCount(); ++row)
            for (size_t col = 0; col < table.ColCount(); ++col)
                out << int(table.Cell(row, col).Align) << ' ' << std::quoted(table.Cell(row, col).Text) << ' ';
        return out.str();
    }
    inline std::unique_ptr<TableEntity> Import(const std::string& data, Object::ObjectID id, const TextStyleTable& styles)
    {
        if (!data.starts_with("WanluoTableV1 ")) return nullptr;
        std::istringstream in(data); in.imbue(std::locale::classic()); std::string marker, style;
        Math::Point3 p; double height = 0, rotation = 0, margin = 0; size_t rows = 0, cols = 0;
        if (!(in >> marker >> p.x >> p.y >> p.z >> height >> rotation >> margin >> rows >> cols >> std::quoted(style))) return nullptr;
        if (!std::isfinite(p.x) || !std::isfinite(p.y) || !std::isfinite(p.z) || !std::isfinite(rotation) || !std::isfinite(margin) || margin < 0) return nullptr;
        // Every serialized row/column consumes at least a character. Bound allocation by the actual input.
        if (!rows || !cols || rows > data.size() || cols > data.size() / rows || !std::isfinite(height) || height <= 0) return nullptr;
        std::vector<double> heights(rows), widths(cols);
        for (double& value : heights) if (!(in >> value) || !std::isfinite(value) || value <= 0) return nullptr;
        for (double& value : widths) if (!(in >> value) || !std::isfinite(value) || value <= 0) return nullptr;
        auto styleId = styles.FindByName(style);
        auto result = std::make_unique<TableEntity>(id, p, std::move(widths), std::move(heights), height, styleId == TextStyleTable::InvalidID ? 0 : styleId);
        result->SetRotation(rotation); result->SetMargin(margin);
        for (size_t row = 0; row < rows; ++row)
            for (size_t col = 0; col < cols; ++col)
            {
                int align = 0; std::string text;
                if (!(in >> align >> std::quoted(text)) || align < 0 || align > 2) return nullptr;
                result->SetCellAlign(row, col, static_cast<TableAlign>(align)); result->SetCellText(row, col, std::move(text));
            }
        return result;
    }
    inline std::unique_ptr<MiniDWG::Entity> Export(const TableEntity& table, MiniDWG::CadDatabase& db, MiniDWG::Handle textStyle, const std::string& styleName)
    {
        std::string name = "*WTABLE" + std::to_string(table.GetID());
        while (db.FindTableEntry<MiniDWG::BlockRecord>(db.BlockRecords(), name)) name += "_";
        auto* block = db.CreateBlockRecord(name);
        if (!block) return nullptr;
        if (auto* begin = db.FindAs<MiniDWG::Block>(block->BlockEntityHandle))
        { begin->Flags = MiniDWG::BlockTypeFlags::Anonymous; begin->Comments = Data(table, styleName); }
        CadDimensionExchange::GraphicsSink graphics(db, block, textStyle); table.Draw(graphics, false, false);
        auto result = std::make_unique<MiniDWG::Insert>(); result->BlockHandle = block->ObjectHandle;
        result->XScale = result->YScale = result->ZScale = 1;
        return result;
    }
}
