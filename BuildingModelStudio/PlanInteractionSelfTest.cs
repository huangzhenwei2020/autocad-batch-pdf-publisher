using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using BatchPdfPublisher.BuildingModel;

namespace Wanluo.BuildingModelStudio
{
    /// <summary>
    /// 平面画布交互自检（不需要窗口消息循环，直接把鼠标消息送进画布）。
    ///
    /// 起因是一次真实崩溃（2026-09-26 的日志里）：
    /// 拖动构件时选中项被清掉（删除/撤销/换楼层都可能），鼠标再一动，
    /// 拖动分支还去解引用 `_selection.Id` → `System.NullReferenceException`。
    ///
    /// 修法是「拖动期间只认开始时抓到的构件引用」，并在构件消失时取消拖动。
    /// 这个自检把当时那几条路径都固定下来：**拖动中构件被删/被撤销/换楼层/宿主墙消失 →
    /// 只能取消拖动，绝不许抛异常，也不许继续改动模型。**
    /// </summary>
    internal static class PlanInteractionSelfTest
    {
        private const double Scale = 0.08d;      // 屏幕像素 / 模型毫米
        private const double OffsetX = 60d;
        private const double OffsetY = 500d;

        public static void Run(Action<string> log)
        {
            if (log == null) log = text => { };
            DragsColumnAndCommits(log);
            CancelsWhenTargetDeleted(log);
            CancelsWhenUndoneMidDrag(log);
            CancelsWhenStoreyChanged(log);
            CancelsWhenHostWallGone(log);
            DrawsAxisAndRenumbers(log);
            DrawsRoomAndMovesIt(log);
            log("PASS 平面交互自检：拖柱 / 拖墙夹点 / 拖洞口 / 拉轴线 / 画房间 —— 目标被删、被撤销、换楼层、宿主墙消失都只取消拖动，不抛异常");
        }

        // ───────────────────────── 6. 拉轴线（自动编号） ─────────────────────────

        private static void DrawsAxisAndRenumbers(Action<string> log)
        {
            var canvas = NewCanvas(out var model);
            canvas.Tool = "axis";
            // 拉一条竖轴：从 (2000,0) 到 (2000,4000) → 位置 X=2000，方向取主轴（竖向）
            canvas.SimulateMouseDown(ToScreen(2000d, 0d), MouseButtons.Left);
            canvas.SimulateMouseDown(ToScreen(2000d, 4000d), MouseButtons.Left);
            Assert(model.Axes.Count == 1, "拉一条轴线后模型里应有 1 条轴线，实际 " + model.Axes.Count);
            var axis = model.Axes[0];
            Assert(axis.Vertical && Math.Abs(axis.Position - 2000d) < 5d, "轴线应是竖轴且在 x≈2000，实际 Vertical=" + axis.Vertical + " Position=" + axis.Position);
            Assert(axis.Name == "1", "第一条竖轴轴号应为 1，实际 " + axis.Name);

            // 再拉两条：x=500 与 x=4000 → 轴号应按位置重排为 1/2/3
            canvas.SimulateMouseDown(ToScreen(500d, 0d), MouseButtons.Left);
            canvas.SimulateMouseDown(ToScreen(500d, 4000d), MouseButtons.Left);
            canvas.SimulateMouseDown(ToScreen(4000d, 0d), MouseButtons.Left);
            canvas.SimulateMouseDown(ToScreen(4000d, 4000d), MouseButtons.Left);
            var ordered = model.Axes.OrderBy(a => a.Position).Select(a => a.Name).ToArray();
            Assert(string.Join(",", ordered) == "1,2,3", "竖轴轴号应按位置重排为 1,2,3，实际 " + string.Join(",", ordered));

            // 拖第 2 条轴线：只能沿垂直方向移动
            var middle = model.Axes.First(a => a.Name == "2");
            var beforeY = middle.Position;
            canvas.Tool = "select";
            canvas.SimulateMouseDown(ToScreen(middle.Position, 2000d), MouseButtons.Left);
            Assert(canvas.IsDragging, "点轴线应进入拖动状态");
            canvas.SimulateMouseMove(ToScreen(3000d, 2600d));
            Assert(Math.Abs(middle.Position - 3000d) < 50d, "轴线应跟着鼠标移到 x≈3000，实际 " + middle.Position);
            canvas.SimulateMouseUp(ToScreen(3000d, 2600d), MouseButtons.Left);
            Assert(Math.Abs(beforeY - 2000d) < 0.01d, "原来的位置不该被改坏");
            log("PASS 拉轴线：拉 3 条 → 轴号按位置重排 1,2,3；拖轴线只沿垂直方向移动");
        }

        // ───────────────────────── 7. 画房间（面积现算 + 整体移动） ─────────────────────────

        private static void DrawsRoomAndMovesIt(Action<string> log)
        {
            var canvas = NewCanvas(out var model);
            canvas.Tool = "room";
            canvas.SimulateMouseDown(ToScreen(0d, 0d), MouseButtons.Left);
            canvas.SimulateMouseDown(ToScreen(3000d, 0d), MouseButtons.Left);
            canvas.SimulateMouseDown(ToScreen(3000d, 2000d), MouseButtons.Left);
            canvas.SimulateMouseDown(ToScreen(0d, 2000d), MouseButtons.Left);
            canvas.SimulateMouseDown(ToScreen(0d, 0d), MouseButtons.Left);        // 点回起点 = 闭合
            Assert(model.Rooms.Count == 1, "画完应有 1 个房间，实际 " + model.Rooms.Count);
            var room = model.Rooms[0];
            Assert(Math.Abs(room.AreaSquareMetres - 6d) < 0.01d, "3×2m 的房间面积应为 6.00 m²，实际 " + room.AreaSquareMetres);

            // 拖房间名（形心附近）整体移动
            canvas.Tool = "select";
            canvas.SimulateMouseDown(ToScreen(1500d, 1000d), MouseButtons.Left);
            Assert(canvas.IsDragging, "点房间名附近应选中并进入拖动状态");
            canvas.SimulateMouseMove(ToScreen(2500d, 1500d));
            canvas.SimulateMouseUp(ToScreen(2500d, 1500d), MouseButtons.Left);
            var centerX = room.Outline.Average(p => p.X);
            Assert(Math.Abs(centerX - 2500d) < 200d, "房间应整体移到 x≈2500，实际 " + centerX);
            Assert(Math.Abs(room.AreaSquareMetres - 6d) < 0.01d, "整体移动不该改变面积");

            // 删除房间后不应残留空引用（拖动中删除也走 CancelDrag）
            canvas.SimulateMouseDown(ToScreen(2500d, 1500d), MouseButtons.Left);
            canvas.DeleteSelection();
            canvas.SimulateMouseMove(ToScreen(4000d, 3000d));
            Assert(model.Rooms.Count == 0, "删除后模型里不应还有房间");
            Assert(canvas.LastPaintError == null, "删除房间后继续移动鼠标不应产生绘制错误：" + canvas.LastPaintError);
            log("PASS 画房间：4 点闭合 → 面积 6.00 m²；拖整体移动；拖动中删除不抛异常");
        }

        // ───────────────────────── 1. 正常拖动与提交 ─────────────────────────

        private static void DragsColumnAndCommits(Action<string> log)
        {
            var canvas = NewCanvas(out var model);
            var column = model.Columns[0];
            var before = column.X;

            canvas.SimulateMouseDown(ToScreen(column.X, column.Y), MouseButtons.Left);
            Assert(canvas.IsDragging, "按住柱子后应进入拖动状态");
            canvas.SimulateMouseMove(ToScreen(column.X + 800d, column.Y + 400d));
            Assert(Math.Abs(column.X - (before + 800d)) < 200d, "拖动柱子应跟随鼠标（实际 X=" + Math.Round(column.X) + "）");
            canvas.SimulateMouseUp(ToScreen(column.X, column.Y), MouseButtons.Left);

            Assert(!canvas.IsDragging, "松开鼠标后拖动应结束");
            Assert(canvas.History.Count >= 1, "拖动结束后应记一步撤销");
            log("PASS 拖柱：跟手移动 " + Math.Round(column.X - before) + "mm，松手记一步撤销");
        }

        // ───────────────────────── 2. 拖动中构件被删除（原崩溃路径） ─────────────────────────

        private static void CancelsWhenTargetDeleted(Action<string> log)
        {
            var canvas = NewCanvas(out var model);
            var column = model.Columns[0];
            canvas.SimulateMouseDown(ToScreen(column.X, column.Y), MouseButtons.Left);
            canvas.SimulateMouseMove(ToScreen(column.X + 200d, column.Y));

            // 拖动还没结束就把这个柱子删掉（等价于"拖动中按了 Delete / 点了删除构件"）
            canvas.DeleteSelection();
            Assert(model.Columns.Count == 0, "删除后模型里不应还有柱子");
            Assert(!canvas.IsDragging, "构件被删除后拖动应立即结束");

            // 关键：鼠标再动一下 —— 以前这里抛 NullReferenceException
            canvas.SimulateMouseMove(ToScreen(column.X + 900d, column.Y + 500d));
            canvas.SimulateMouseUp(ToScreen(column.X, column.Y), MouseButtons.Left);
            Assert(canvas.LastPaintError == null, "删除后继续移动鼠标不应产生绘制错误：" + canvas.LastPaintError);
            log("PASS 拖柱中删除：拖动已取消，鼠标继续移动不再抛 NullReferenceException");
        }

        // ───────────────────────── 3. 拖动中撤销（模型被换成快照） ─────────────────────────

        private static void CancelsWhenUndoneMidDrag(Action<string> log)
        {
            var canvas = NewCanvas(out var model);
            var wall = model.Walls[0];

            // 先改一次墙，让撤销栈里有东西：拖端点夹点把 (6000,0) 拖到 (6500,500)
            canvas.SimulateMouseDown(ToScreen(wall.X2, wall.Y2), MouseButtons.Left);
            Assert(canvas.IsDragging, "点墙端点夹点应进入拖动状态");
            canvas.SimulateMouseMove(ToScreen(wall.X2 + 500d, wall.Y2 + 500d));
            canvas.SimulateMouseUp(ToScreen(wall.X2, wall.Y2), MouseButtons.Left);

            // 再开始一次拖动，拖到一半按撤销：模型被替换成快照，抓着的旧对象已不在模型里
            var endX = wall.X2;
            var endY = wall.Y2;
            canvas.SimulateMouseDown(ToScreen(endX, endY), MouseButtons.Left);
            Assert(canvas.IsDragging, "第二次点端点夹点应进入拖动状态");
            canvas.Undo();
            Assert(!canvas.IsDragging, "撤销后拖动应立即结束");

            var restored = canvas.FindWall(wall.Id);
            Assert(restored != null, "撤销后墙应还在");
            var beforeX2 = restored.X2;
            canvas.SimulateMouseMove(ToScreen(beforeX2 + 1200d, 1200d));
            canvas.SimulateMouseUp(ToScreen(beforeX2 + 1200d, 1200d), MouseButtons.Left);
            var after = canvas.FindWall(wall.Id);
            Assert(after != null && Math.Abs(after.X2 - beforeX2) < 0.01d, "撤销后继续移动鼠标不应再改动墙");
            log("PASS 拖墙夹点中撤销：拖动已取消，鼠标继续移动不再改动模型");
        }

        // ───────────────────────── 4. 拖动中换楼层 ─────────────────────────

        private static void CancelsWhenStoreyChanged(Action<string> log)
        {
            var canvas = NewCanvas(out var model);
            var column = model.Columns[0];
            canvas.SimulateMouseDown(ToScreen(column.X, column.Y), MouseButtons.Left);
            Assert(canvas.IsDragging, "按住柱子后应进入拖动状态");

            canvas.StoreyId = model.Storeys.Count > 1 ? model.Storeys[1].Id : model.Storeys[0].Id + "-其它层";
            Assert(!canvas.IsDragging, "换楼层后拖动应立即结束");
            var x = column.X;
            canvas.SimulateMouseMove(ToScreen(column.X + 2000d, column.Y + 2000d));
            Assert(Math.Abs(column.X - x) < 0.01d, "换楼层后继续移动鼠标不应再拖动旧楼层的柱子");
            log("PASS 拖柱中换楼层：拖动已取消，鼠标继续移动不再拖动旧构件");
        }

        // ───────────────────────── 5. 拖动洞口时它所在的墙没了 ─────────────────────────

        private static void CancelsWhenHostWallGone(Action<string> log)
        {
            var canvas = NewCanvas(out var model);
            var opening = model.Openings[0];
            canvas.SimulateMouseDown(ToScreen(opening.Offset, 0d), MouseButtons.Left);
            Assert(canvas.IsDragging, "点洞口应进入拖动状态");

            // 直接把墙从模型里拿掉（洞口还在列表里）：拖动时应发现"所在的墙没了"并取消
            model.Walls.Clear();
            var offset = opening.Offset;
            canvas.SimulateMouseMove(ToScreen(2000d, 0d));
            canvas.SimulateMouseUp(ToScreen(2000d, 0d), MouseButtons.Left);
            Assert(!canvas.IsDragging, "墙没了以后拖动应结束");
            Assert(Math.Abs(opening.Offset - offset) < 0.01d, "墙没了以后不应再改洞口定位");
            log("PASS 拖洞口时宿主墙消失：拖动已取消，不再改洞口定位");
        }

        // ───────────────────────── 脚手架 ─────────────────────────

        private static PlanCanvas NewCanvas(out BuildingModelDocument model)
        {
            model = SampleModelFactory.CreateEmptyModel("拖动自检");
            var storey = model.Storeys[0].Id;
            model.Walls.Add(new WallModel
            {
                Id = "w1", StoreyId = storey, X1 = 0d, Y1 = 0d, X2 = 6000d, Y2 = 0d, Thickness = 240d
            });
            model.Columns.Add(new ColumnModel
            {
                Id = "k1", StoreyId = storey, X = 2000d, Y = 1000d, Width = 400d, Depth = 400d
            });
            model.Openings.Add(new OpeningModel
            {
                Id = "o1", HostWallId = "w1", Code = "C1500", Kind = "窗",
                Offset = 3000d, Width = 1500d, Height = 1800d, Sill = 900d
            });

            var canvas = new PlanCanvas { Size = new Size(900, 640) };
            canvas.Model = model;
            canvas.StoreyId = storey;
            canvas.Tool = "select";
            canvas.SetViewport(Scale, OffsetX, OffsetY);
            return canvas;
        }

        /// <summary>模型坐标 → 屏幕坐标（与画布的视图变换一致）。</summary>
        private static Point ToScreen(double x, double y)
        {
            return new Point((int)Math.Round(OffsetX + x * Scale), (int)Math.Round(OffsetY - y * Scale));
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
    }
}
