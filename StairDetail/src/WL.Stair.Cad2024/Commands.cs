using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using WL.Stair.Core.Calculation;
using WL.Stair.Core.Geometry;
using WL.Stair.Core.Validation;

namespace WL.Stair.Cad2024
{
    public sealed class Commands
    {
        [CommandMethod("LTDY", CommandFlags.Modal | CommandFlags.NoBlockEditor)]
        public void GenerateStairDetail()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var promptService = new PromptService(editor);
            WL.Stair.Core.Domain.StairDefinition definition;

            if (!promptService.TryGetDefinition(out definition))
            {
                editor.WriteMessage("\n已取消生成楼梯大样。\n");
                return;
            }

            var outcome = new StairCalculator().Calculate(definition);
            WriteIssues(editor, outcome);

            if (!outcome.IsSuccess)
            {
                editor.WriteMessage("\n参数存在错误，未生成图形。\n");
                return;
            }

            var pointResult = editor.GetPoint("\n指定楼梯平面大样插入点: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n已取消生成楼梯大样。\n");
                return;
            }

            try
            {
                var geometryBuilder = new StairGeometryBuilder();
                var firstFloorPlan = geometryBuilder.BuildPlan(
                    definition,
                    outcome.Result,
                    StairPlanLevel.FirstFloor);
                var intermediateFloorPlan = geometryBuilder.BuildPlan(
                    definition,
                    outcome.Result,
                    StairPlanLevel.IntermediateFloor);
                var topFloorPlan = geometryBuilder.BuildPlan(
                    definition,
                    outcome.Result,
                    StairPlanLevel.TopFloor);
                var section = geometryBuilder.BuildSection(definition, outcome.Result);
                var renderer = new CadLineRenderer();
                var planSpacing = outcome.Result.PlanWidth + 1000.0;
                var intermediateFloorPoint = pointResult.Value + new Vector3d(0.0, -planSpacing, 0.0);
                var topFloorPoint = pointResult.Value + new Vector3d(0.0, -(planSpacing * 2.0), 0.0);
                var sectionPoint = pointResult.Value + new Vector3d(
                    outcome.Result.PlanLength + 2000.0,
                    -outcome.Result.FloorElevation,
                    0.0);

                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    renderer.Render(document.Database, transaction, firstFloorPlan, pointResult.Value);
                    renderer.Render(document.Database, transaction, intermediateFloorPlan, intermediateFloorPoint);
                    renderer.Render(document.Database, transaction, topFloorPlan, topFloorPoint);
                    renderer.Render(document.Database, transaction, section, sectionPoint);
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\n楼梯大样已生成：{0} 个踢面，踢面高 {1:F1}，踏步宽 {2:F1}。\n",
                    outcome.Result.TotalRiserCount,
                    outcome.Result.RiserHeight,
                    definition.TreadDepth);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n生成失败：{0}\n", exception.Message);
            }
        }

        private static void WriteIssues(Editor editor, StairCalculationOutcome outcome)
        {
            foreach (var issue in outcome.Issues)
            {
                var severity = issue.Severity == ValidationSeverity.Error ? "错误" : "提示";
                editor.WriteMessage(
                    "\n[{0}][{1}] {2}\n",
                    severity,
                    issue.Code,
                    issue.Message);
            }
        }
    }
}
