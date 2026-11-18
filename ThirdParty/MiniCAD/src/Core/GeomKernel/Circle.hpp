#pragma once
#include "../Math/Point3.hpp"
#include "../Math/Constants.hpp"
#include "AABB.hpp"
#include "../Math/OCS.hpp"
#include <cmath>
#include <cstdlib>

namespace MiniCAD
{
    struct Circle
    {
        Math::Point3 Center;
        double       Radius = 0.0;
        Math::Vec3 Normal{0,0,1};

        Circle() = default;

        constexpr Circle(const Math::Point3& center, double radius, const Math::Vec3& normal = {0,0,1})
            : Center(center)
            , Radius(radius)
            , Normal(normal)
        {
        }

        // 圆是否有效（半径大于容差）
        bool IsValid() const { return Radius > Math::LengthEPS; }

        // 圆上角度 angle（弧度）对应的点坐标
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

        double ProjectedRadius(const Math::Point3& point) const
        {
            const auto delta = point - Center;
            const auto local = Math::WcsToOcs(Math::BuildOCS(Normal), {delta.x,delta.y,delta.z});
            return std::hypot(local.x, local.y);
        }

        // 四个象限点：East / North / West / South
        Math::Point3 QuadrantE() const { return PointAt(0.0); }
        Math::Point3 QuadrantN() const { return PointAt(Math::PI * 0.5); }
        Math::Point3 QuadrantW() const { return PointAt(Math::PI); }
        Math::Point3 QuadrantS() const { return PointAt(Math::PI * 1.5); }

        // 点 p 在圆上的最近点
        Math::Point3 ClosestPoint(const Math::Point3& p) const
        {
            double dist = ProjectedRadius(p);
            if (dist < Math::LengthEPS)
                return QuadrantE();   // 退化：p 在圆心，任意返回 East 点
            return PointAt(AngleAt(p));
        }

        // 点 p 到圆周的距离（非圆内距离，是到弧线的距离）
        double DistanceToPoint(const Math::Point3& p) const
        {
            const auto delta = p - ClosestPoint(p);
            return std::hypot(delta.x, delta.y, delta.z);
        }

        // 轴对齐包围盒
        AABB GetBounds() const
        {
            const auto frame = Math::BuildOCS(Normal);
            const Math::Vec3 extent{Radius * std::hypot(frame.Ux.x, frame.Uy.x),
                Radius * std::hypot(frame.Ux.y, frame.Uy.y), Radius * std::hypot(frame.Ux.z, frame.Uy.z)};
            return {Center - extent, Center + extent};
        }
    };
}
