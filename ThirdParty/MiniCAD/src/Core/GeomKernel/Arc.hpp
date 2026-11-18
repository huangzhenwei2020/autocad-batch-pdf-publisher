#pragma once
#include "../Math/Point3.hpp"
#include "../Math/Constants.hpp"
#include "AABB.hpp"
#include "../Math/OCS.hpp"
#include <cmath>
#include <algorithm>
#include <optional>

namespace MiniCAD
{
    struct Arc
    {
        Math::Point3 Center;
        double       Radius     = 0.0;
        double       StartAngle = 0.0;  // 弧度，逆时针，从 +X 轴开始
        double       EndAngle   = 0.0;  // 弧度，逆时针
        Math::Vec3 Normal{0,0,1};

        // ----------------------------------------------------------------
        // 构造
        // ----------------------------------------------------------------
        Arc() = default;

        constexpr Arc(const Math::Point3& center, double radius, double startAngle, double endAngle, const Math::Vec3& normal = {0,0,1})
            : Center(center)
            , Radius(radius)
            , StartAngle(startAngle)
            , EndAngle(endAngle)
            , Normal(normal)
        {
        }

        // 三点法构造：p1=起点, p2=弧上一点, p3=终点
        // 失败（三点共线）时返回 std::nullopt
        static std::optional<Arc> FromThreePoints(const Math::Point3& p1, const Math::Point3& p2, const Math::Point3& p3)
        {
            // ── Step 1  外接圆圆心（二维，z 取 p1.z）──────────────────────
            //
            //  公式推导：三点 A/B/C 的外接圆满足
            //    D  = 2(ax(by-cy) + bx(cy-ay) + cx(ay-by))
            //    ux = (|A|²(by-cy) + |B|²(cy-ay) + |C|²(ay-by)) / D
            //    uy = (|A|²(cx-bx) + |B|²(ax-cx) + |C|²(bx-ax)) / D
            //
            double ax = p1.x, ay = p1.y;
            double bx = p2.x, by = p2.y;
            double cx = p3.x, cy = p3.y;

            double D = 2.0 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (std::abs(D) < Math::LengthEPS)
                return std::nullopt;    // 三点共线，无法构成圆弧

            double a2 = ax * ax + ay * ay;
            double b2 = bx * bx + by * by;
            double c2 = cx * cx + cy * cy;

            double ux = (a2 * (by - cy) + b2 * (cy - ay) + c2 * (ay - by)) / D;
            double uy = (a2 * (cx - bx) + b2 * (ax - cx) + c2 * (bx - ax)) / D;

            Math::Point3 center{ ux, uy, p1.z };
            double radius = std::sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy));

            // ── Step 2  各点对应的极角 ────────────────────────────────────
            double angle1 = std::atan2(ay - uy, ax - ux);   // p1 的极角
            double angle3 = std::atan2(cy - uy, cx - ux);   // p3 的极角

            // ── Step 3  用叉积判断 p1→p2→p3 的绕向 ──────────────────────
            //
            //  cross_z > 0 → 逆时针（CCW）：直接以 angle1→angle3 存储，
            //                               弧自然经过 p2
            //  cross_z < 0 → 顺时针（CW）：反转为 angle3→angle1，
            //                               使存储始终为 CCW 并仍经过 p2
            //
            double cross = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);

            if (cross >= 0.0)
                return Arc(center, radius, angle1, angle3);
            else
                return Arc(center, radius, angle3, angle1);
        }

        // ----------------------------------------------------------------
        // 基本查询
        // ----------------------------------------------------------------

        bool IsValid() const
        {
            return Radius > Math::LengthEPS &&
                std::abs(SweepAngle()) > Math::AngleEPS;
        }

        // 逆时针角跨度，始终在 (0, TwoPI]
        double SweepAngle() const
        {
            double sweep = EndAngle - StartAngle;
            if (sweep <= 0.0)
                sweep += Math::TwoPI;
            return sweep;
        }

        double MidAngle() const { return StartAngle + SweepAngle() * 0.5; }

        Math::Point3 PointAt(double angle) const
        {
            const auto frame = Math::BuildOCS(Normal);
            return Center + frame.Ux * (std::cos(angle) * Radius) + frame.Uy * (std::sin(angle) * Radius);
        }

        double AngleAt(const Math::Point3& point) const
        {
            const auto delta = point - Center;
            const auto local = Math::WcsToOcs(Math::BuildOCS(Normal), {delta.x,delta.y,delta.z});
            return std::atan2(local.y, local.x);
        }

        Math::Point3 StartPoint() const { return PointAt(StartAngle); }
        Math::Point3 EndPoint()   const { return PointAt(EndAngle); }
        Math::Point3 MidPoint()   const { return PointAt(MidAngle()); }

        // 角度是否在弧范围内（逆时针判断）
        bool ContainsAngle(double angle) const
        {
            double rel = std::fmod(angle - StartAngle, Math::TwoPI);
            if (rel < 0.0) rel += Math::TwoPI;
            return rel < SweepAngle();
        }

        // ----------------------------------------------------------------
        // 几何计算
        // ----------------------------------------------------------------

        Math::Point3 ClosestPoint(const Math::Point3& p) const
        {
            double angle = AngleAt(p);

            if (ContainsAngle(angle))
                return PointAt(angle);

            // 投影角在弧外，返回最近端点
            Math::Point3 s = StartPoint();
            Math::Point3 e = EndPoint();
            const auto startDelta = p - s, endDelta = p - e;
            double ds = startDelta.LengthSq();
            double de = endDelta.LengthSq();
            return ds <= de ? s : e;
        }

        double DistanceToPoint(const Math::Point3& p) const
        {
            Math::Point3 cp = ClosestPoint(p);
            const auto delta = p - cp;
            return std::hypot(delta.x, delta.y, delta.z);
        }

        // 世界坐标包围盒，检查各坐标分量沿圆弧的极值。
        AABB GetBounds() const
        {
            AABB bounds;
            bounds.Expand(StartPoint());
            bounds.Expand(EndPoint());
            const auto frame = Math::BuildOCS(Normal);
            const double extrema[] = {std::atan2(frame.Uy.x, frame.Ux.x),
                std::atan2(frame.Uy.y, frame.Ux.y), std::atan2(frame.Uy.z, frame.Ux.z)};
            for (double angle : extrema)
                for (double candidate : {angle, angle + Math::PI})
                    if (ContainsAngle(candidate)) bounds.Expand(PointAt(candidate));
            return bounds;
        }
    };
}
