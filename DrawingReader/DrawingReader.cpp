#include "Database/CadDatabase.h"
#include "Database/CadFileFormat.h"
#include "Dwg/Read/DwgReader.h"
#include "Dxf/Read/DxfReader.h"
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <iterator>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>
#include <vector>

using namespace MiniDWG;

namespace
{
    constexpr double Pi = 3.14159265358979323846;

    std::string Json(std::string_view value)
    {
        std::ostringstream result;
        result << '"';
        for (const unsigned char character : value)
        {
            if (character == '"' || character == '\\') result << '\\' << character;
            else if (character < 32) result << "\\u" << std::hex << std::setw(4) << std::setfill('0') << int(character) << std::dec;
            else result << character;
        }
        result << '"';
        return result.str();
    }

    struct Transform
    {
        double A = 1, B = 0, C = 0, D = 1, X = 0, Y = 0;
        XY Apply(double x, double y) const { return { A * x + C * y + X, B * x + D * y + Y }; }
        Transform Then(const Transform& local) const
        {
            return { A * local.A + C * local.B, B * local.A + D * local.B,
                     A * local.C + C * local.D, B * local.C + D * local.D,
                     A * local.X + C * local.Y + X, B * local.X + D * local.Y + Y };
        }
    };

    struct PreviewLine { XY Start, End; std::string Layer; };
    struct PreviewText { XY Position; double Height; std::string Value, Layer; };

    class Preview
    {
    public:
        const CadDatabase& Database;
        std::vector<PreviewLine> Lines;
        std::vector<PreviewText> Texts;
        std::map<std::string, int> Unsupported;
        std::set<Handle> Blocks;

        explicit Preview(const CadDatabase& database) : Database(database) { }

        void LineTo(XY start, XY end, const Transform& transform, const std::string& layer)
        {
            start = transform.Apply(start.X, start.Y);
            end = transform.Apply(end.X, end.Y);
            if (std::isfinite(start.X) && std::isfinite(start.Y) && std::isfinite(end.X) && std::isfinite(end.Y))
                Lines.push_back({ start, end, layer });
        }

        void ArcTo(XY center, double radius, double start, double sweep, const Transform& transform, const std::string& layer)
        {
            if (!std::isfinite(radius) || !std::isfinite(sweep) || !std::isfinite(start) || radius <= 0 || std::abs(sweep) > 2 * Pi + 0.001)
                return;
            const int steps = std::max(2, int(std::ceil(std::abs(sweep) / (Pi / 36))));
            XY previous{ center.X + radius * std::cos(start), center.Y + radius * std::sin(start) };
            for (int index = 1; index <= steps; ++index)
            {
                const double angle = start + sweep * index / steps;
                XY next{ center.X + radius * std::cos(angle), center.Y + radius * std::sin(angle) };
                LineTo(previous, next, transform, layer);
                previous = next;
            }
        }

        void PolylineEdge(const LwPolylineVertex& start, const LwPolylineVertex& end, const Transform& transform, const std::string& layer)
        {
            const double dx = end.Location.X - start.Location.X, dy = end.Location.Y - start.Location.Y;
            const double bulge = start.Bulge;
            if (std::abs(bulge) < 1e-12)
            {
                LineTo(start.Location, end.Location, transform, layer);
                return;
            }
            const double chord = std::hypot(dx, dy);
            if (!std::isfinite(bulge) || chord < 1e-12) return;
            const double offset = chord * (1 - bulge * bulge) / (4 * bulge);
            XY center{ (start.Location.X + end.Location.X) / 2 - dy * offset / chord,
                       (start.Location.Y + end.Location.Y) / 2 + dx * offset / chord };
            ArcTo(center, chord * (1 + bulge * bulge) / (4 * std::abs(bulge)),
                  std::atan2(start.Location.Y - center.Y, start.Location.X - center.X), 4 * std::atan(bulge), transform, layer);
        }

        void EntityTo(Handle handle, const Transform& transform, const std::string& inheritedLayer = {})
        {
            auto entity = Database.FindAs<Entity>(handle);
            if (!entity || entity->IsInvisible) return;
            const auto layerRecord = Database.FindAs<Layer>(entity->LayerHandle);
            const std::string layer = layerRecord && layerRecord->Name != "0" ? layerRecord->Name : inheritedLayer.empty() ? "0" : inheritedLayer;
            if (const auto line = dynamic_cast<const Line*>(entity))
                LineTo({ line->StartPoint.X, line->StartPoint.Y }, { line->EndPoint.X, line->EndPoint.Y }, transform, layer);
            else if (const auto arc = dynamic_cast<const Arc*>(entity))
            {
                double sweep = std::fmod(arc->EndAngle - arc->StartAngle, 2 * Pi);
                if (sweep <= 0) sweep += 2 * Pi;
                ArcTo({ arc->Center.X, arc->Center.Y }, arc->Radius, arc->StartAngle, sweep, transform, layer);
            }
            else if (const auto circle = dynamic_cast<const Circle*>(entity))
                ArcTo({ circle->Center.X, circle->Center.Y }, circle->Radius, 0, 2 * Pi, transform, layer);
            else if (const auto polyline = dynamic_cast<const LwPolyline*>(entity))
            {
                for (std::size_t index = 1; index < polyline->Vertices.size(); ++index)
                    PolylineEdge(polyline->Vertices[index - 1], polyline->Vertices[index], transform, layer);
                if (polyline->Vertices.size() > 1 && (static_cast<int>(polyline->Flags) & 1))
                    PolylineEdge(polyline->Vertices.back(), polyline->Vertices.front(), transform, layer);
            }
            else if (const auto text = dynamic_cast<const TextEntity*>(entity))
            {
                const auto point = transform.Apply(text->InsertPoint.X, text->InsertPoint.Y);
                const double height = text->Height * std::hypot(transform.C, transform.D);
                if (std::isfinite(point.X) && std::isfinite(point.Y) && std::isfinite(height) && height > 0)
                    Texts.push_back({ point, height, text->Value, layer });
            }
            else if (const auto insert = dynamic_cast<const Insert*>(entity))
            {
                const auto block = Database.FindAs<BlockRecord>(insert->BlockHandle);
                if (!block || Blocks.size() >= 64 || !Blocks.insert(insert->BlockHandle).second)
                {
                    ++Unsupported["INSERT_REFERENCE"];
                    return;
                }
                const auto definition = Database.FindAs<Block>(block->BlockEntityHandle);
                const XYZ origin = definition ? definition->BasePoint : XYZ{};
                if (std::uint64_t(insert->ColumnCount) * insert->RowCount > 10000)
                    ++Unsupported["INSERT_LARGE_ARRAY"];
                else for (int row = 0; row < std::max(1, int(insert->RowCount)); ++row)
                    for (int column = 0; column < std::max(1, int(insert->ColumnCount)); ++column)
                    {
                        const double cosine = std::cos(insert->Rotation), sine = std::sin(insert->Rotation);
                        Transform local{ cosine * insert->XScale, sine * insert->XScale, -sine * insert->YScale, cosine * insert->YScale };
                        local.X = insert->InsertPoint.X - local.A * origin.X - local.C * origin.Y + cosine * column * insert->ColumnSpacing - sine * row * insert->RowSpacing;
                        local.Y = insert->InsertPoint.Y - local.B * origin.X - local.D * origin.Y + sine * column * insert->ColumnSpacing + cosine * row * insert->RowSpacing;
                        for (const auto child : block->Entities) EntityTo(child, transform.Then(local), layer);
                    }
                Blocks.erase(insert->BlockHandle);
                for (const auto attribute : insert->Attributes) EntityTo(attribute, transform, layer);
            }
            else ++Unsupported[std::string(entity->GetDxfName())];
        }
    };

    void WriteSpace(std::ostream& output, const CadDatabase& database, const std::string& name, const BlockRecord* block)
    {
        Preview preview(database);
        if (block) for (const auto entity : block->Entities) preview.EntityTo(entity, {});
        output << "{\"name\":" << Json(name) << ",\"lines\":[";
        bool comma = false;
        for (const auto& line : preview.Lines)
        {
            if (comma) output << ',';
            comma = true;
            output << "{\"x1\":" << line.Start.X << ",\"y1\":" << line.Start.Y << ",\"x2\":" << line.End.X << ",\"y2\":" << line.End.Y << ",\"layer\":" << Json(line.Layer) << '}';
        }
        output << "],\"texts\":[";
        comma = false;
        for (const auto& text : preview.Texts)
        {
            if (comma) output << ',';
            comma = true;
            output << "{\"x\":" << text.Position.X << ",\"y\":" << text.Position.Y << ",\"height\":" << text.Height << ",\"value\":" << Json(text.Value) << ",\"layer\":" << Json(text.Layer) << '}';
        }
        output << "],\"unsupported\":{";
        comma = false;
        for (const auto& [type, count] : preview.Unsupported)
        {
            if (comma) output << ',';
            comma = true;
            output << Json(type) << ':' << count;
        }
        output << "}}";
    }
}

int wmain(int argc, wchar_t** argv)
{
    try
    {
        if (argc != 3) throw std::runtime_error("Expected input drawing and output snapshot paths.");
        std::ifstream input(std::filesystem::path(argv[1]), std::ios::binary);
        if (!input) throw std::runtime_error("The drawing could not be opened.");
        std::vector<std::uint8_t> bytes((std::istreambuf_iterator<char>(input)), {});
        const auto format = DetectFileFormat(bytes);
        std::unique_ptr<CadDatabase> database;
        if (format.format == CadFileFormat::Dwg) database = ReadDwg(bytes);
        else if (format.format == CadFileFormat::DxfAscii || format.format == CadFileFormat::DxfBinary) database = ReadDxf(bytes);
        if (!database) throw std::runtime_error("Unsupported or damaged DWG/DXF drawing.");
        std::ofstream output(std::filesystem::path(argv[2]), std::ios::binary);
        if (!output) throw std::runtime_error("The preview output could not be created.");
        output.imbue(std::locale::classic());
        output << std::setprecision(17) << "{\"schemaVersion\":1,\"objectCount\":" << database->Objects().size() << ",\"layers\":[";
        bool comma = false;
        for (const auto& [handle, object] : database->Objects())
            if (const auto layer = dynamic_cast<const Layer*>(object.get()))
            {
                if (comma) output << ',';
                comma = true;
                output << "{\"name\":" << Json(layer->Name) << ",\"visible\":" << (layer->IsOn ? "true" : "false") << '}';
            }
        output << "],\"spaces\":[";
        WriteSpace(output, *database, "模型", database->ModelSpace());
        std::vector<const Layout*> layouts;
        for (const auto& [handle, object] : database->Objects())
            if (const auto layout = dynamic_cast<const Layout*>(object.get()); layout && layout->AssociatedBlockHandle != (database->ModelSpace() ? database->ModelSpace()->ObjectHandle : 0))
                layouts.push_back(layout);
        std::stable_sort(layouts.begin(), layouts.end(), [](const Layout* left, const Layout* right) { return left->TabOrder < right->TabOrder; });
        for (const auto layout : layouts)
        {
            output << ',';
            WriteSpace(output, *database, layout->Name, database->FindAs<BlockRecord>(layout->AssociatedBlockHandle));
        }
        output << "]}";
        output.close();
        if (!output) throw std::runtime_error("The preview output could not be completed.");
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << std::endl;
        return 1;
    }
}
