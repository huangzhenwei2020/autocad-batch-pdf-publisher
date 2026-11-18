#pragma once
#include "Circle.hpp"
#include "Arc.hpp"

namespace MiniCAD
{
    // Rigid transforms express their new basis in WCS, then recover canonical OCS angles.
    template<class TransformPoint>
    Circle TransformSpatialCircle(const Circle& source, TransformPoint transform)
    {
        const auto frame = Math::BuildOCS(source.Normal);
        const auto center = transform(source.Center);
        const auto x = transform(source.Center + frame.Ux) - center;
        const auto y = transform(source.Center + frame.Uy) - center;
        return {center, source.Radius, Math::Cross(x, y).Normalized()};
    }

    template<class TransformPoint>
    Arc TransformSpatialArc(const Arc& source, TransformPoint transform)
    {
        const auto circle = TransformSpatialCircle(Circle{source.Center, source.Radius, source.Normal}, transform);
        const double start = circle.AngleAt(transform(source.StartPoint()));
        return {circle.Center, circle.Radius, start, start + source.SweepAngle(), circle.Normal};
    }
}
