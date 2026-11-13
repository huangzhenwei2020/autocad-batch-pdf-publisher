#pragma once
#include "Core/Entity/DimensionEntity.hpp"
#include "Database/CadDatabase.h"
#include "Database/DimensionMeasurement.h"
#include <iomanip>
#include <sstream>

namespace MiniCAD::CadDimensionExchange
{
    inline MiniDWG::XYZ Point(const Math::Point3& p) { return {p.x, p.y, p.z}; }
    inline Math::Point3 Point(const MiniDWG::XYZ& p) { return {p.X, p.Y, p.Z}; }

    // Anonymous CAD dimension blocks carry the exact on-screen graphics.
    class GraphicsSink : public IDrawSink
    {
    public:
        GraphicsSink(MiniDWG::CadDatabase& db, MiniDWG::BlockRecord* block, MiniDWG::Handle style)
            : m_db(db), m_block(block), m_style(style) {}
        void DrawLine(const Math::Point3& a, const Math::Point3& b, const Math::Color4&, bool) override
        {
            auto line = std::make_unique<MiniDWG::Line>(); line->StartPoint = Point(a); line->EndPoint = Point(b);
            line->Color = MiniDWG::Color::ByBlock(); m_db.AddEntity(m_block, std::move(line));
        }
        void FillTriangle(const Math::Point3& a, const Math::Point3& b, const Math::Point3& c, const Math::Color4&) override
        {
            auto triangle = std::make_unique<MiniDWG::Solid>(); triangle->FirstCorner = Point(a); triangle->SecondCorner = Point(b);
            triangle->ThirdCorner = Point(c); triangle->FourthCorner = Point(c);
            triangle->Color = MiniDWG::Color::ByBlock(); m_db.AddEntity(m_block, std::move(triangle));
        }
        void EmitMText(const Math::Point3& p, const std::string& value, uint32_t, double height, double rotation, double width, const Math::Color4&) override
        {
            auto text = std::make_unique<MiniDWG::MText>(); text->InsertPoint = Point(p); text->Value = value;
            text->Height = height; text->RectangleWidth = width; text->AlignmentPoint = {std::cos(rotation), std::sin(rotation), 0};
            text->AttachmentPoint = MiniDWG::AttachmentPointType::BottomLeft;
            text->StyleHandle = m_style; text->Color = MiniDWG::Color::ByBlock(); m_db.AddEntity(m_block, std::move(text));
        }
    private:
        MiniDWG::CadDatabase& m_db;
        MiniDWG::BlockRecord* m_block;
        MiniDWG::Handle m_style;
    };

    inline std::string JoggedData(const DimensionEntity& dim)
    {
        std::ostringstream out; out.imbue(std::locale::classic()); out << std::setprecision(17) << "WanluoJoggedV1 ";
        for (const auto& p : {dim.GetCenterPoint(), dim.GetP1(), dim.GetP2(), dim.GetDimLinePoint()}) out << p.x << ' ' << p.y << ' ' << p.z << ' ';
        const auto& s = dim.GetStyle();
        out << s.TextHeight << ' ' << s.ArrowSize << ' ' << s.ExtLineExtend << ' ' << s.ExtLineOffset << ' ' << s.TextGap << ' ' << s.Precision << ' ' << int(s.Terminator) << ' ' << std::quoted(dim.GetTextOverride());
        return out.str();
    }
    inline std::unique_ptr<DimensionEntity> ReadJogged(const std::string& data, Object::ObjectID id)
    {
        std::istringstream in(data); in.imbue(std::locale::classic()); std::string marker; in >> marker;
        if (marker != "WanluoJoggedV1") return nullptr;
        Math::Point3 center, first, replacement, bend;
        for (auto* p : {&center, &first, &replacement, &bend})
            if (!(in >> p->x >> p->y >> p->z) || !std::isfinite(p->x) || !std::isfinite(p->y) || !std::isfinite(p->z)) return nullptr;
        DimStyle style; int terminator = 0; std::string text;
        if (!(in >> style.TextHeight >> style.ArrowSize >> style.ExtLineExtend >> style.ExtLineOffset >> style.TextGap >> style.Precision >> terminator >> std::quoted(text))) return nullptr;
        if (!std::isfinite(style.TextHeight) || style.TextHeight <= 0) return nullptr;
        for (double value : {style.ArrowSize, style.ExtLineExtend, style.ExtLineOffset, style.TextGap}) if (!std::isfinite(value) || value < 0) return nullptr;
        if (style.Precision < 0 || style.Precision > 15) return nullptr;
        style.Terminator = terminator == 1 ? DimTerminator::Oblique : DimTerminator::Arrow;
        auto result = DimensionEntity::MakeJoggedRadius(id, center, first, replacement, bend, style);
        result->SetTextOverride(text); return result;
    }

    inline std::unique_ptr<MiniDWG::Entity> Export(const DimensionEntity& source, MiniDWG::CadDatabase& db, MiniDWG::Handle textStyle)
    {
        namespace D = MiniDWG;
        std::string blockName = "*DWL" + std::to_string(source.GetID());
        while (db.FindTableEntry<D::BlockRecord>(db.BlockRecords(), blockName)) blockName += "_";
        auto* block = db.CreateBlockRecord(blockName);
        if (!block) return nullptr;
        if (auto* begin = db.FindAs<D::Block>(block->BlockEntityHandle))
        {
            begin->Flags = D::BlockTypeFlags::Anonymous;
            begin->Comments = source.GetType() == DimType::JoggedRadius ? JoggedData(source) : "WanluoDimensionV1";
        }
        GraphicsSink graphics(db, block, textStyle); source.Draw(graphics, false, false);
        if (source.GetType() == DimType::JoggedRadius)
        {
            auto insert = std::make_unique<D::Insert>(); insert->BlockHandle = block->ObjectHandle;
            insert->XScale = insert->YScale = insert->ZScale = 1;
            return insert;
        }
        const auto first = Point(source.GetP1()), second = Point(source.GetP2()), center = Point(source.GetCenterPoint());
        const auto line = Point(source.GetDimLinePoint());
        std::unique_ptr<D::Dimension> result;
        switch (source.GetType())
        {
            case DimType::Linear:
            {
                auto dim = std::make_unique<D::DimensionLinear>(); dim->FirstPoint = first; dim->SecondPoint = second; dim->Rotation = source.GetLinearAngle();
                dim->Flags = D::DimensionType::Linear; result = std::move(dim); break;
            }
            case DimType::Aligned:
            {
                auto dim = std::make_unique<D::DimensionAligned>(); dim->FirstPoint = first; dim->SecondPoint = second;
                dim->Flags = D::DimensionType::Aligned; result = std::move(dim); break;
            }
            case DimType::Angular:
            {
                auto dim = std::make_unique<D::DimensionAngular3Pt>(); dim->FirstPoint = first; dim->SecondPoint = second; dim->AngleVertex = center;
                dim->Flags = D::DimensionType::Angular3Point; result = std::move(dim); break;
            }
            case DimType::Radius:
            {
                auto dim = std::make_unique<D::DimensionRadius>(); dim->DefinitionPoint = center; dim->AngleVertex = first;
                dim->Flags = D::DimensionType::Radius; result = std::move(dim); break;
            }
            case DimType::Diameter:
            {
                auto dim = std::make_unique<D::DimensionDiameter>(); dim->DefinitionPoint = first; dim->AngleVertex = second;
                dim->Flags = D::DimensionType::Diameter; result = std::move(dim); break;
            }
            case DimType::Ordinate:
            {
                auto dim = std::make_unique<D::DimensionOrdinate>(); dim->FeatureLocation = first; dim->LeaderEndpoint = line;
                dim->Flags = D::DimensionType::Ordinate;
                if (source.GetOrdinateAxis() == OrdinateAxis::X) dim->Flags = dim->Flags | D::DimensionType::OrdinateTypeX;
                result = std::move(dim); break;
            }
            case DimType::ArcLength:
            {
                auto dim = std::make_unique<D::DimensionArc>(); dim->Center = center; dim->FirstPoint = first; dim->SecondPoint = second;
                dim->StartAngle = std::atan2(first.Y - center.Y, first.X - center.X);
                dim->EndAngle = std::atan2(second.Y - center.Y, second.X - center.X);
                dim->Flags = D::DimensionType::Aligned; result = std::move(dim); break;
            }
            default: return nullptr;
        }
        if (source.GetType() != DimType::Radius && source.GetType() != DimType::Diameter && source.GetType() != DimType::Ordinate) result->DefinitionPoint = line;
        auto style = std::make_unique<D::DimensionStyle>();
        style->Name = "WL_DIM_" + std::to_string(db.DimensionStyles()->Entries.size());
        while (db.FindTableEntry<D::DimensionStyle>(db.DimensionStyles(), style->Name)) style->Name += "_";
        const auto& s = source.GetStyle();
        style->TextHeight = s.TextHeight; style->ArrowSize = s.ArrowSize; style->ExtensionLineExtension = s.ExtLineExtend;
        style->ExtensionLineOffset = s.ExtLineOffset; style->DimensionLineGap = s.TextGap; style->DecimalPlaces = static_cast<int16_t>(s.Precision);
        style->AngularDecimalPlaces = static_cast<int16_t>(s.AnglePrecision); style->StyleHandle = textStyle;
        style->TickSize = s.Terminator == DimTerminator::Oblique ? s.ArrowSize : 0;
        D::DimensionStyle* matching = nullptr;
        for (auto handle : db.DimensionStyles()->Entries)
        {
            auto* candidate = db.FindAs<D::DimensionStyle>(handle);
            if (candidate && candidate->Name.starts_with("WL_DIM_") && candidate->TextHeight == style->TextHeight
                && candidate->ArrowSize == style->ArrowSize && candidate->ExtensionLineExtension == style->ExtensionLineExtension
                && candidate->ExtensionLineOffset == style->ExtensionLineOffset && candidate->DimensionLineGap == style->DimensionLineGap
                && candidate->DecimalPlaces == style->DecimalPlaces && candidate->AngularDecimalPlaces == style->AngularDecimalPlaces
                && candidate->StyleHandle == style->StyleHandle && candidate->TickSize == style->TickSize)
            { matching = candidate; break; }
        }
        if (!matching) matching = db.AddTableEntry(db.DimensionStyles(), std::move(style));
        result->StyleHandle = matching ? matching->ObjectHandle : D::kNullHandle;
        result->BlockHandle = block->ObjectHandle; result->Text = source.GetTextOverride(); result->TextMiddlePoint = Point(source.TextPosition());
        result->Flags = result->Flags | D::DimensionType::BlockReference; result->Measurement = source.Measurement();
        if (!source.UsesDefaultTextPosition()) result->Flags = result->Flags | D::DimensionType::TextUserDefinedLocation;
        return result;
    }

    inline std::unique_ptr<DimensionEntity> Import(const MiniDWG::Dimension& source, Object::ObjectID id, const DimStyle& style)
    {
        namespace D = MiniDWG;
        std::unique_ptr<DimensionEntity> result;
        if (auto* dim = dynamic_cast<const D::DimensionLinear*>(&source)) result = DimensionEntity::MakeLinear(id, Point(dim->FirstPoint), Point(dim->SecondPoint), Point(dim->DefinitionPoint), dim->Rotation, style);
        else if (auto* dim = dynamic_cast<const D::DimensionAligned*>(&source)) result = DimensionEntity::MakeAligned(id, Point(dim->FirstPoint), Point(dim->SecondPoint), Point(dim->DefinitionPoint), style);
        else if (auto* dim = dynamic_cast<const D::DimensionAngular3Pt*>(&source)) result = DimensionEntity::MakeAngular(id, Point(dim->AngleVertex), Point(dim->FirstPoint), Point(dim->SecondPoint), Point(dim->DefinitionPoint), style);
        else if (auto* dim = dynamic_cast<const D::DimensionRadius*>(&source)) result = DimensionEntity::MakeRadius(id, Point(dim->DefinitionPoint), Point(dim->AngleVertex), style);
        else if (auto* dim = dynamic_cast<const D::DimensionDiameter*>(&source)) result = DimensionEntity::MakeDiameter(id, Point(dim->DefinitionPoint), Point(dim->AngleVertex), style);
        else if (auto* dim = dynamic_cast<const D::DimensionOrdinate*>(&source)) result = DimensionEntity::MakeOrdinate(id, Point(dim->FeatureLocation), Point(dim->LeaderEndpoint), D::HasFlag(dim->Flags, D::DimensionType::OrdinateTypeX) ? OrdinateAxis::X : OrdinateAxis::Y, style);
        else if (auto* dim = dynamic_cast<const D::DimensionArc*>(&source)) result = DimensionEntity::MakeArcLength(id, Point(dim->Center), Point(dim->FirstPoint), Point(dim->SecondPoint), Point(dim->DefinitionPoint), style);
        if (result)
        {
            result->SetTextOverride(source.Text);
            if (D::HasFlag(source.Flags, D::DimensionType::TextUserDefinedLocation)) result->SetTextPosition(Point(source.TextMiddlePoint));
            result->Translate(Point(source.InsertionPoint) - Math::Point3{});
        }
        return result;
    }
}
