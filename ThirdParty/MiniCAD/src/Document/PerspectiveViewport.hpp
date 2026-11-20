#pragma once
#include "Core/Entity/ViewportEntity.hpp"
#include "Core/Math/WorkingPlane.hpp"
#include "Document/WipeClip.hpp"
#include <array>
#include <limits>
#include <optional>

namespace MiniCAD
{
    // Project model-space primitives into a floating VIEWPORT's paper coordinates.
    // Clip in camera space before dividing by depth, including at the eye plane.
    class PerspectiveViewport
    {
    public:
        explicit PerspectiveViewport(const ViewportEntity& viewport)
            : m_center(viewport.GetCenter()), m_modelCenter(viewport.GetViewCenter()), m_halfWidth(viewport.GetWidth() * .5),
              m_halfHeight(viewport.GetHeight() * .5)
        {
            const auto [right, up] = viewport.ViewAxes();
            m_right = right;
            m_up = up;
            m_normal = Math::Cross(right, up);
            const auto& optics = viewport.GetCameraOptics();
            m_lens = optics.Lens > 0 && std::isfinite(optics.Lens) ? optics.Lens : 50.0;
            // AutoCAD's lens is defined against the diagonal of the view field (42 mm).
            m_focalPaper = std::hypot(viewport.GetWidth(), viewport.GetHeight()) * m_lens / 42.0;
            const auto direction = viewport.GetViewDirection() - Math::Point3{};
            m_eye = viewport.HasSpatialView() ? direction.Length() : m_focalPaper / viewport.ModelToPaperScale();
            m_near = std::max(1e-8, m_eye * 1e-7);
            if (optics.FrontEnabled && !optics.FrontAtEye && std::isfinite(optics.Front))
                m_near = std::max(m_near, m_eye - optics.Front);
            if (optics.BackEnabled && std::isfinite(optics.Back))
                m_far = m_eye - optics.Back;
        }

        bool Valid() const
        {
            return m_halfWidth > 0 && m_halfHeight > 0 && std::isfinite(m_focalPaper) &&
                m_focalPaper > 0 && std::isfinite(m_eye) && m_eye > 0 && m_far >= m_near;
        }

        double Lens() const { return m_lens; }

        // On the plane through the view target, depth equals the eye distance.
        Math::Vec3 TargetPlaneOffset(double paperX, double paperY) const
        {
            return (m_right * paperX + m_up * paperY) * (m_eye / m_focalPaper);
        }

        std::optional<Math::Point3> ModelPointToPaper(const Math::Point3& point) const
        {
            const auto view = ToView(point - m_modelCenter);
            if (!std::isfinite(view.x) || !std::isfinite(view.y) || !std::isfinite(view.z)) return {};
            for (size_t plane = 0; plane < kPlaneCount; ++plane)
                if (Distance(view, plane) < 0) return {};
            const auto scale = m_focalPaper / view.z;
            return Math::Point3{m_center.x + view.x * scale, m_center.y + view.y * scale, m_center.z};
        }

        std::optional<Math::Point3> PaperPointOnPlane(const Math::Point3& point, const Math::WorkingPlane& plane) const
        {
            const auto ray = m_right * ((point.x - m_center.x) / m_focalPaper) +
                             m_up * ((point.y - m_center.y) / m_focalPaper) - m_normal;
            const auto eye = m_normal * m_eye;
            const auto normal = plane.Normal();
            const double dot = Math::Dot(ray, normal);
            if (std::abs(dot) <= 1e-10 * ray.Length()) return {};
            const double depth = Math::Dot((plane.Origin - m_modelCenter) - eye, normal) / dot;
            if (!std::isfinite(depth) || depth < m_near || depth > m_far) return {};
            const auto result = m_modelCenter + (eye + ray * depth);
            if (!std::isfinite(result.x) || !std::isfinite(result.y) || !std::isfinite(result.z)) return {};
            return result;
        }

        bool Point(Vertex_P3_C4& vertex, const Math::Point3& paperOrigin) const
        {
            const auto view = ToView(vertex.pos);
            for (size_t plane = 0; plane < kPlaneCount; ++plane)
                if (Distance(view, plane) < 0) return false;
            vertex.pos = ToPaper(view, paperOrigin);
            return true;
        }

        bool Line(Vertex_P3_C4& start, Vertex_P3_C4& end, const Math::Point3& paperOrigin) const
        {
            const auto a = ToView(start.pos), b = ToView(end.pos);
            double first = 0, last = 1;
            for (size_t plane = 0; plane < kPlaneCount; ++plane)
            {
                const double da = Distance(a, plane), db = Distance(b, plane);
                if (da < 0 && db < 0) return false;
                if (da < 0) first = std::max(first, da / (da - db));
                else if (db < 0) last = std::min(last, da / (da - db));
            }
            if (first > last) return false;
            const auto begin = start;
            start = WipeClip::detail::Lerp(begin, end, first);
            end = WipeClip::detail::Lerp(begin, end, last);
            start.pos = ToPaper(a + (b - a) * first, paperOrigin);
            end.pos = ToPaper(a + (b - a) * last, paperOrigin);
            return true;
        }

        template<typename Vertex, typename Emit>
        void Triangle(const Vertex& a, const Vertex& b, const Vertex& c,
                      const Math::Point3& paperOrigin, Emit emit) const
        {
            struct Corner { Vertex Data; Math::Point3 View; };
            // A triangle clipped against six planes gains at most six vertices.
            std::array<Corner, 9> polygon{{{a, ToView(a.pos)}, {b, ToView(b.pos)}, {c, ToView(c.pos)}}};
            std::array<Corner, 9> next{};
            size_t count = 3;
            for (size_t plane = 0; plane < kPlaneCount && count >= 3; ++plane)
            {
                size_t written = 0;
                for (size_t i = 0; i < count; ++i)
                {
                    const auto& from = polygon[i];
                    const auto& to = polygon[(i + 1) % count];
                    const double df = Distance(from.View, plane), dt = Distance(to.View, plane);
                    if (df >= 0) next[written++] = from;
                    if ((df > 0 && dt < 0) || (df < 0 && dt > 0))
                    {
                        const double t = df / (df - dt);
                        next[written++] = {WipeClip::detail::Lerp(from.Data, to.Data, t), from.View + (to.View - from.View) * t};
                    }
                }
                polygon.swap(next);
                count = written;
            }
            if (count < 3) return;
            for (size_t i = 0; i < count; ++i)
                polygon[i].Data.pos = ToPaper(polygon[i].View, paperOrigin);
            for (size_t i = 1; i + 1 < count; ++i)
                emit(polygon[0].Data, polygon[i].Data, polygon[i + 1].Data);
        }

    private:
        Math::Point3 ToView(const Math::Float3& point) const
        {
            return ToView(Math::Vec3{point.x, point.y, point.z});
        }

        Math::Point3 ToView(const Math::Vec3& offset) const
        {
            return {Math::Dot(m_right, offset), Math::Dot(m_up, offset), m_eye - Math::Dot(m_normal, offset)};
        }

        Math::Float3 ToPaper(const Math::Point3& point, const Math::Point3& origin) const
        {
            const auto center = m_center - origin;
            const double scale = m_focalPaper / std::max(point.z, m_near);
            return {static_cast<float>(center.x + point.x * scale),
                    static_cast<float>(center.y + point.y * scale), static_cast<float>(center.z)};
        }

        double Distance(const Math::Point3& point, size_t plane) const
        {
            switch (plane)
            {
            case 0: return point.z - m_near;
            case 1: return m_far - point.z;
            case 2: return m_halfWidth * point.z + m_focalPaper * point.x;
            case 3: return m_halfWidth * point.z - m_focalPaper * point.x;
            case 4: return m_halfHeight * point.z + m_focalPaper * point.y;
            default: return m_halfHeight * point.z - m_focalPaper * point.y;
            }
        }

        static constexpr size_t kPlaneCount = 6;
        Math::Point3 m_center, m_modelCenter;
        Math::Vec3 m_right, m_up, m_normal;
        double m_halfWidth, m_halfHeight;
        double m_lens = 0, m_focalPaper = 0, m_eye = 0, m_near = 0;
        double m_far = std::numeric_limits<double>::infinity();
    };
}
