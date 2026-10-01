using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using BatchPdfPublisher.BuildingModel;
using BatchPdfPublisher.Models;
using BatchPdfPublisher.Services;

namespace BatchPdfPublisher.Features.BuildingModel.Services
{
    internal static class CadRegisteredDoorWindowSchedule
    {
        public static long SaveSills(string modelPath, long version, IEnumerable<DoorWindowScheduleItem> items)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return version;
            if (File.GetLastWriteTimeUtc(CadFloorPlanRegistry.FilePath(modelPath)).Ticks != version)
                throw new InvalidOperationException("楼层登记已更新，请先加载最新楼层登记门窗表，再保存立面参数。");
            var registry = CadFloorPlanRegistry.Load(modelPath);
            var model = BuildingModelJson.LoadModel(modelPath);
            registry.UpdateScheduleSills(model, items);
            return File.GetLastWriteTimeUtc(CadFloorPlanRegistry.FilePath(modelPath)).Ticks;
        }
        public static DoorWindowScheduleReadResult Load()
        {
            var project = new PublishPlanStore().GetActiveProject();
            var path = StudioLaunch.ActiveModelPath(project?.ProjectFolder, project?.Name);
            if (path == null) return null;
            var registry = CadFloorPlanRegistry.Load(path);
            if (registry.Floors.Count == 0) return null;
            var model = BuildingModelJson.LoadModel(path);
            foreach (var capture in registry.Floors) capture.ValidateSchedule();
            var result = new DoorWindowScheduleReadResult { SourceDxfName = "CAD 楼层登记门窗表", Adapter = model.Name,
                Diagnostic = path, CadRegistrationVersion = File.GetLastWriteTimeUtc(CadFloorPlanRegistry.FilePath(path)).Ticks };
            result.Items.AddRange(registry.BuildSchedule(model));
            foreach (var storey in model.Storeys.Where(s => registry.Find(string.IsNullOrWhiteSpace(s.TemplateStoreyId) ? s.Id : s.TemplateStoreyId) != null))
                result.FloorColumns.Add(new DoorWindowFloorColumn { FloorName = storey.Name, FloorCount = 1 });
            return result;
        }
    }
}
