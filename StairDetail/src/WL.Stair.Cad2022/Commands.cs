using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using WL.Stair.Core.Calculation;
using WL.Stair.Core.Domain;
using WL.Stair.Core.Geometry;
using WL.Stair.Core.Validation;

namespace WL.Stair.Cad2022
{
    public sealed class Commands
    {
        [CommandMethod("LTDY", CommandFlags.Modal)]
        public void GenerateStairDetail()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var editor = document.Editor;
            var settingsWindow = new StairSettingsWindow();
            if (Application.ShowModalWindow(settingsWindow) != true)
            {
                editor.WriteMessage("\n已取消生成楼梯大样。\n");
                return;
            }

            GenerateProject(
                document,
                settingsWindow.Project,
                settingsWindow.ConfirmedCalculation,
                null);
        }

        [CommandMethod("WLSTAIRTEST", CommandFlags.Modal)]
        public void GenerateStairDetailForTest()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            GenerateProject(document, StairProjectDefinition.CreateDefault(), null, Point3d.Origin);
        }

        private static void GenerateProject(
            Document document,
            StairProjectDefinition project,
            StairProjectCalculationResult confirmedCalculation,
            Point3d? fixedInsertionPoint)
        {
            var editor = document.Editor;
            var calculation = confirmedCalculation;
            if (calculation == null)
            {
                var outcome = new StairProjectCalculator().Calculate(project);
                WriteIssues(editor, outcome);
                if (!outcome.IsSuccess)
                {
                    editor.WriteMessage("\n参数存在错误，未生成图形。\n");
                    return;
                }
                calculation = outcome.Result;
            }

            Point3d insertionPoint;
            if (fixedInsertionPoint.HasValue)
            {
                insertionPoint = fixedInsertionPoint.Value;
            }
            else
            {
                var pointResult = editor.GetPoint("\n指定楼梯大样插入点: ");
                if (pointResult.Status != PromptStatus.OK)
                {
                    editor.WriteMessage("\n已取消生成楼梯大样。\n");
                    return;
                }
                insertionPoint = pointResult.Value;
            }

            try
            {
                var section = new StairProjectGeometryBuilder().BuildSection(project, calculation);
                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    new CadLineRenderer().Render(
                        document.Database,
                        transaction,
                        section,
                        insertionPoint);
                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\n构件化楼梯剖面已生成：{0} 个楼层段，总高度 {1:F0} mm。\n",
                    calculation.Storeys.Count,
                    calculation.TotalHeight);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n生成失败：{0}\n", exception.Message);
            }
        }

        private static void Generate(
            Document document,
            StairDefinition definition,
            int floorCount,
            Point3d? fixedInsertionPoint)
        {
            var editor = document.Editor;

            var outcome = new StairCalculator().Calculate(definition);
            WriteIssues(editor, outcome);

            if (!outcome.IsSuccess)
            {
                editor.WriteMessage("\n参数存在错误，未生成图形。\n");
                return;
            }

            Point3d insertionPoint;
            if (fixedInsertionPoint.HasValue)
            {
                insertionPoint = fixedInsertionPoint.Value;
            }
            else
            {
                var pointResult = editor.GetPoint("\n指定楼梯大样插入点: ");
                if (pointResult.Status != PromptStatus.OK)
                {
                    editor.WriteMessage("\n已取消生成楼梯大样。\n");
                    return;
                }

                insertionPoint = pointResult.Value;
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
                var section = geometryBuilder.BuildMultiFloorSection(
                    definition,
                    outcome.Result,
                    floorCount);
                var renderer = new CadLineRenderer();
                var planSpacing = outcome.Result.PlanWidth + 1000.0;
                var intermediateFloorPoint = insertionPoint + new Vector3d(0.0, -planSpacing, 0.0);
                var topFloorPoint = insertionPoint + new Vector3d(0.0, -(planSpacing * 2.0), 0.0);
                var sectionPoint = insertionPoint + new Vector3d(
                    outcome.Result.PlanLength + 2000.0,
                    -(planSpacing * 2.0),
                    0.0);

                using (var transaction = document.Database.TransactionManager.StartTransaction())
                {
                    renderer.Render(document.Database, transaction, firstFloorPlan, insertionPoint);
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
                editor.WriteMessage("\n[{0}][{1}] {2}\n", severity, issue.Code, issue.Message);
            }
        }

        private static void WriteIssues(Editor editor, StairProjectCalculationOutcome outcome)
        {
            foreach (var issue in outcome.Issues)
            {
                var severity = issue.Severity == ValidationSeverity.Error ? "错误" : "提示";
                editor.WriteMessage("\n[{0}][{1}] {2}: {3}\n", severity, issue.Code, issue.ParameterName, issue.Message);
            }
        }
    }
}
