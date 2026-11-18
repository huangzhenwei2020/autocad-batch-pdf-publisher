#pragma once
#include "Document/WipeClip.hpp"
#include <stdexcept>

template<class Vertex>
double ClippedArea(const std::vector<Vertex>& vertices)
{
    double area = 0;
    for (size_t i = 0; i + 2 < vertices.size(); i += 3)
    {
        const auto a = vertices[i].pos, b = vertices[i + 1].pos, c = vertices[i + 2].pos;
        area += std::abs((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) / 2;
    }
    return area;
}

inline void CheckDetailedClipping()
{
    using namespace MiniCAD;
    auto check = [](bool condition, const char* message)
    { if (!condition) throw std::runtime_error(message); };
    const WipeClip::Poly rectangle({{0, 0, 0}, {10, 0, 0}, {10, 10, 0}, {0, 10, 0}});
    const WipeClip::Poly concave({{0, 0, 0}, {300, 0, 0}, {300, 200, 0}, {150, 200, 0}, {150, 100, 0}, {0, 100, 0}});
    auto quad = [](float x0, float y0, float x1, float y1)
    {
        std::vector<Vertex_P3_C4_UV> vertices(6);
        const Math::Float2 uv[] = {{0, 0}, {1, 0}, {1, 1}, {0, 0}, {1, 1}, {0, 1}};
        for (size_t i = 0; i < vertices.size(); ++i)
        {
            vertices[i].pos = {x0 + uv[i].x * (x1 - x0), y0 + uv[i].y * (y1 - y0), uv[i].x * 2};
            vertices[i].uv = uv[i];
            vertices[i].color = {uv[i].x, uv[i].y, .25f, .5f};
        }
        return vertices;
    };
    auto verifyUv = [&](const auto& vertices, float x0, float y0, float x1, float y1)
    {
        for (const auto& vertex : vertices)
        {
            check(std::abs(vertex.pos.x - (x0 + vertex.uv.x * (x1 - x0))) < .001 &&
                  std::abs(vertex.pos.y - (y0 + vertex.uv.y * (y1 - y0))) < .001, "Texture clipping changed UV mapping");
            check(std::abs(vertex.pos.z - vertex.uv.x * 2) < .001 && vertex.color.w == .5f &&
                  std::abs(vertex.color.x - vertex.uv.x) < .001, "Texture clipping changed depth/color/alpha");
        }
    };
    auto vertices = quad(-5, -5, 15, 15);
    WipeClip::ClipTexturedInside(vertices, rectangle);
    check(std::abs(ClippedArea(vertices) - 100) < .001 && vertices.size() % 6 == 0, "Textured rectangle clipping area/batches changed");
    verifyUv(vertices, -5, -5, 15, 15);
    vertices = quad(0, 0, 300, 200);
    WipeClip::ClipTexturedInside(vertices, concave);
    check(std::abs(ClippedArea(vertices) - 45000) < .1, "Textured concave clipping failed");
    verifyUv(vertices, 0, 0, 300, 200);
    vertices = quad(13, 2, 9, 6);
    WipeClip::ClipTexturedInside(vertices, rectangle);
    check(std::abs(ClippedArea(vertices) - 4) < .001, "Mirrored texture with outside center disappeared");
    verifyUv(vertices, 13, 2, 9, 6);
    auto prefix = quad(100, 100, 102, 102);
    const auto crossing = quad(-5, -5, 15, 15);
    vertices = prefix;
    vertices.insert(vertices.end(), crossing.begin(), crossing.end());
    WipeClip::ClipTexturedInside(vertices, rectangle, 6);
    check(vertices[0].pos.x == 100 && std::abs(ClippedArea(vertices) - 104) < .001, "Texture clipping changed an earlier stream prefix");

    auto image = std::make_shared<ImageData>();
    image->Key = 123;
    ImageDraw source;
    source.Image = image;
    std::copy(crossing.begin(), crossing.end(), source.Verts);
    std::vector<ImageDraw> images{source};
    WipeClip::ClipImages(images, rectangle);
    double imageArea = 0;
    for (const auto& part : images)
    {
        check(part.Image == image, "Clipping duplicated the decoded image");
        const std::vector<Vertex_P3_C4_UV> partVertices(std::begin(part.Verts), std::end(part.Verts));
        imageArea += ClippedArea(partVertices);
        verifyUv(partVertices, -5, -5, 15, 15);
    }
    check(std::abs(imageArea - 100) < .001, "Image clipping area changed");

    Glyph glyph;
    glyph.Filled = true;
    glyph.Triangles = {{{0, 0, 0}, {1, 0, 0}, {1, 1, 0}}, {{0, 0, 0}, {1, 1, 0}, {0, 1, 0}}};
    glyph.Lines.emplace_back(Math::Point3{0, .5, 0}, Math::Point3{1, .5, 0});
    GlyphMeshLibrary library;
    uint32_t mesh;
    check(library.Register(glyph, mesh), "Synthetic glyph registration failed");
    const GlyphInstance inside{2, 0, 0, 2, 2, 2, 0, {1, 0, 0, .5f}, mesh};
    auto outside = inside;
    outside.tx = outside.ty = 20;
    const GlyphInstance edge{20, 0, 0, 20, -5, -5, 0, inside.color, mesh};
    std::vector<GlyphInstance> instances{outside, inside, outside, edge};
    std::vector<Vertex_P3_C4> lines, fills;
    WipeClip::ClipViewportGlyphs(instances, rectangle, 1, library, lines, fills);
    check(instances.size() == 2 && instances[0].tx == 20 && instances[1].tx == 2, "Contained glyph/prefix instancing changed");
    check(std::abs(ClippedArea(fills) - 100) < .001 && lines.size() == 2 &&
          std::abs(lines[0].pos.x) < .001 && std::abs(lines[1].pos.x - 10) < .001, "Boundary glyph was dropped or not cut");
    for (const auto& vertex : fills) check(vertex.color == inside.color, "Glyph clipping changed its color");
    instances = {{0, 20, -20, 0, 15, -5, 0, inside.color, mesh}};
    lines.clear(); fills.clear();
    WipeClip::ClipViewportGlyphs(instances, rectangle, 0, library, lines, fills);
    check(instances.empty() && std::abs(ClippedArea(fills) - 100) < .001, "Rotated glyph clipping failed");
    instances = {{300, 0, 0, 200, 0, 0, 0, inside.color, mesh}};
    lines.clear(); fills.clear();
    WipeClip::ClipViewportGlyphs(instances, concave, 0, library, lines, fills);
    check(instances.empty() && std::abs(ClippedArea(fills) - 45000) < .1, "Concave glyph clipping failed");
    instances.assign(10000, inside);
    lines.clear(); fills.clear();
    WipeClip::ClipViewportGlyphs(instances, rectangle, 0, library, lines, fills);
    check(instances.size() == 10000 && lines.empty() && fills.empty() && library.View().Meshes.size() == 1,
          "Contained text lost shared instancing");
}
