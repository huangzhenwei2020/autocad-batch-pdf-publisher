#pragma once
#include "Point3.hpp"
#include <cmath>

namespace MiniCAD::Math
{
    struct WorkingPlane
    {
        Point3 Origin{};
        Vec3 X{1, 0, 0};
        Vec3 Y{0, 1, 0};

        Vec3 Normal() const
        {
            return {X.y * Y.z - X.z * Y.y, X.z * Y.x - X.x * Y.z, X.x * Y.y - X.y * Y.x};
        }
        static double Dot(const Vec3& a, const Vec3& b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
        bool Set(const Point3& origin, Vec3 x, Vec3 y)
        {
            const double lengthX = std::hypot(x.x, x.y, x.z);
            if (!std::isfinite(origin.x) || !std::isfinite(origin.y) || !std::isfinite(origin.z) ||
                !std::isfinite(lengthX) || lengthX < 1e-12) return false;
            x = x * (1 / lengthX);
            y = y - x * Dot(x, y);
            const double lengthY = std::hypot(y.x, y.y, y.z);
            if (!std::isfinite(lengthY) || lengthY < 1e-12) return false;
            Origin = origin;
            X = x;
            Y = y * (1 / lengthY);
            return true;
        }
        Point3 ToLocal(const Point3& world) const
        {
            const auto delta = world - Origin;
            return {Dot(delta, X), Dot(delta, Y), Dot(delta, Normal())};
        }
        Vec3 ToWorldVector(double x, double y, double z = 0) const { return X * x + Y * y + Normal() * z; }
        Point3 ToWorld(const Point3& local) const { return Origin + ToWorldVector(local.x, local.y, local.z); }
        bool IsWorld() const
        {
            return std::hypot(Origin.x, Origin.y, Origin.z) < 1e-9 &&
                std::hypot(X.x - 1, X.y, X.z) < 1e-9 && std::hypot(Y.x, Y.y - 1, Y.z) < 1e-9;
        }
    };
}
