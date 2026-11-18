#pragma once
#include "../Math/OCS.hpp"
#include "../Math/Point3.hpp"
#include "../Math/Constants.hpp"
#include "AABB.hpp"
#include <cmath>
#include <algorithm>
#include <utility>

namespace MiniCAD
{
    // 椭圆 / 椭圆弧：圆心 + 半长轴 / 半短轴 + 旋转角（弧度，逆时针）+ 参数区间
    // 参数方程：
    //   P(t) = Center
    //        + RadiusX * cos(t) * [cos(Rotation), sin(Rotation)]
    //        + RadiusY * sin(t) * [-sin(Rotation), cos(Rotation)]
    // 椭圆弧取 t 从 StartParam 逆时针到 EndParam（与 Arc 的角度约定相同，跨度 ∈ (0, 2π]）；
    // 默认 [0, 2π] 即整椭圆。参数是「参数角」，不是几何角（同 DXF 组码 41 / 42）。
    struct Ellipse
    {
        Math::Point3 Center;
        double       RadiusX    = 0.0;          // 沿 Rotation 方向的半轴
        double       RadiusY    = 0.0;          // 垂直于 Rotation 方向的半轴
        double       Rotation   = 0.0;          // X half-axis angle relative to the OCS X axis
        double       StartParam = 0.0;          // 起始参数角
        double       EndParam   = Math::TwoPI;  // 终止参数角
        Math::Vec3   Normal{0, 0, 1};

        Ellipse() = default;

        constexpr Ellipse(const Math::Point3& center,
                          double rx, double ry, double rotation = 0.0,
                          double startParam = 0.0, double endParam = Math::TwoPI,
                          const Math::Vec3& normal = {0, 0, 1})
            : Center(center)
            , RadiusX(rx)
            , RadiusY(ry)
            , Rotation(rotation)
            , StartParam(startParam)
            , EndParam(endParam)
            , Normal(normal)
        {
        }

        // ── 有效性 ──────────────────────────────────────────────────────
        bool IsValid() const
        {
            return RadiusX > Math::LengthEPS && RadiusY > Math::LengthEPS;
        }

        // 是否近似为圆
        bool IsCircular() const
        {
            return std::abs(RadiusX - RadiusY) < Math::LengthEPS;
        }

        // ── 参数区间 ────────────────────────────────────────────────────
        static double WrapParam(double t)
        {
            t = std::fmod(t, Math::TwoPI);
            return t < 0.0 ? t + Math::TwoPI : t;
        }

        // 逆时针参数跨度，始终在 (0, TwoPI]
        double SweepParam() const
        {
            double sweep = EndParam - StartParam;
            if (sweep <= 0.0)
                sweep += Math::TwoPI;
            return std::min(sweep, Math::TwoPI);
        }

        bool IsFull() const { return SweepParam() >= Math::TwoPI - Math::AngleEPS; }

        bool ContainsParam(double t) const
        {
            if (IsFull()) return true;
            return WrapParam(t - StartParam) <= SweepParam() + Math::AngleEPS;
        }

        // 设置参数区间（规范化到 [0, 2π)；跨度为 0 或 2π 视为整椭圆）
        void SetParams(double startParam, double endParam)
        {
            const double s = WrapParam(startParam);
            const double e = WrapParam(endParam);
            if (std::abs(e - s) < Math::AngleEPS) { StartParam = 0.0; EndParam = Math::TwoPI; return; }
            StartParam = s;
            EndParam   = e;
        }

        double       MidParam()   const { return StartParam + SweepParam() * 0.5; }
        Math::Point3 StartPoint() const { return PointAt(StartParam); }
        Math::Point3 EndPoint()   const { return PointAt(StartParam + SweepParam()); }

        std::pair<Math::Vec3, Math::Vec3> Axes() const
        {
            const auto frame = Math::BuildOCS(Normal);
            const double c = std::cos(Rotation), s = std::sin(Rotation);
            return {frame.Ux * c + frame.Uy * s, frame.Uy * c - frame.Ux * s};
        }

        void SetAxisDirection(const Math::Vec3& direction)
        {
            const auto frame = Math::BuildOCS(Normal);
            Rotation = std::atan2(Math::Dot(direction, frame.Uy), Math::Dot(direction, frame.Ux));
        }

        // ── 参数点 ──────────────────────────────────────────────────────
        // 参数 t 为参数角（周期 TwoPI），逆时针
        Math::Point3 PointAt(double t) const
        {
            const auto [x, y] = Axes();
            return Center + x * (RadiusX * std::cos(t)) + y * (RadiusY * std::sin(t));
        }

        // 四个特征点（对应参数 t = 0, PI/2, PI, 3PI/2）
        Math::Point3 VertexE()  const { return PointAt(0.0);            } // 长轴正端
        Math::Point3 VertexN()  const { return PointAt(Math::PI * 0.5); } // 短轴正端
        Math::Point3 VertexW()  const { return PointAt(Math::PI);       } // 长轴负端
        Math::Point3 VertexS()  const { return PointAt(Math::PI * 1.5); } // 短轴负端

        // 世界方向角 → 参数角（点 p 相对圆心，去旋转后按半轴缩放再 atan2）
        double ParamOfPoint(const Math::Point3& p) const
        {
            const auto [x, y] = Axes();
            const auto delta = p - Center;
            const double lx = Math::Dot(delta, x), ly = Math::Dot(delta, y);
            return std::atan2(ly / (RadiusY + Math::LengthEPS), lx / (RadiusX + Math::LengthEPS));
        }

        // ── 最近点 ──────────────────────────────────────────────────────
        // 椭圆弧：整椭圆上的最近点若落在弧外，取较近的端点
        Math::Point3 ClosestPoint(const Math::Point3& p) const
        {
            const double t = ClosestParamFull(p);
            if (ContainsParam(t))
                return PointAt(t);
            const Math::Point3 s = StartPoint();
            const Math::Point3 e = EndPoint();
            const double ds = (p - s).LengthSq(), de = (p - e).LengthSq();
            return ds <= de ? s : e;
        }

        // 整椭圆（忽略参数区间）上最近点的参数角（迭代法，牛顿法 + 参数化）
        // 将点 p 变换到椭圆局部坐标系，然后在局部坐标系中做 1D 参数搜索
        double ClosestParamFull(const Math::Point3& p) const
        {
            // 变换到局部坐标（去旋转）
            const auto [x, y] = Axes();
            const auto delta = p - Center;
            const double lx = Math::Dot(delta, x), ly = Math::Dot(delta, y);

            // 初始猜测：直接用 atan2
            double t = std::atan2(ly / (RadiusY + Math::LengthEPS),  lx / (RadiusX + Math::LengthEPS));

            // 牛顿迭代（最多 8 次，通常 3~4 次收敛）
            for (int i = 0; i < 8; ++i)
            {
                double cosT = std::cos(t);
                double sinT = std::sin(t);
                // 椭圆上的点（局部坐标）
                double ex   = RadiusX * cosT;
                double ey   = RadiusY * sinT;
                // 椭圆的切向量（局部坐标）
                double tx_  = -RadiusX * sinT;
                double ty_  =  RadiusY * cosT;
                // 残差：(P-E) · T = 0
                double f    = (lx - ex) * tx_ + (ly - ey) * ty_;
                // 导数
                double df   = (lx - ex) * (-RadiusX * cosT)  + (-tx_)   * tx_  + (ly - ey) * (-RadiusY * sinT)  + (-ty_)   * ty_;
                if (std::abs(df) < 1e-14) break;
                double dt = f / df;
                t -= dt;
                if (std::abs(dt) < 1e-9) break;
            }
            return t;
        }

        // 点 p 到椭圆周的距离
        double DistanceToPoint(const Math::Point3& p) const
        {
            return (p - ClosestPoint(p)).Length();
        }

        // Extrema of Center + u cos(t) + v sin(t), evaluated in world axes.
        AABB GetBounds() const
        {
            const auto [x, y] = Axes();
            const auto u = x * RadiusX, v = y * RadiusY;
            if (IsFull())
            {
                const Math::Vec3 extent{std::hypot(u.x,v.x), std::hypot(u.y,v.y), std::hypot(u.z,v.z)};
                return {Center - extent, Center + extent};
            }

            AABB box;
            box.Expand(StartPoint());
            box.Expand(EndPoint());
            for (double t : {std::atan2(v.x,u.x), std::atan2(v.y,u.y), std::atan2(v.z,u.z)})
            {
                if (ContainsParam(t)) box.Expand(PointAt(t));
                if (ContainsParam(t + Math::PI)) box.Expand(PointAt(t + Math::PI));
            }
            return box;
        }
    };
}
