using BatchPdfPublisher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;

namespace BatchPdfPublisher.Services
{
    internal sealed class DoorWindowElevationTemplateStore
    {
        private const string FileName = "door-window-elevation-templates.json";
        private static string PathName { get { return UserDataPaths.SettingsFile(FileName); } }

        public List<DoorWindowElevationTemplate> Load()
        {
            try
            {
                if (!File.Exists(PathName))
                {
                    var defaults = CreateDefaults(); Save(defaults); return defaults;
                }
                List<DoorWindowElevationTemplate> loaded;
                using (var stream = File.OpenRead(PathName)) loaded = (List<DoorWindowElevationTemplate>)new DataContractJsonSerializer(typeof(List<DoorWindowElevationTemplate>)).ReadObject(stream) ?? new List<DoorWindowElevationTemplate>();
                var changed = false; foreach (var builtIn in CreateDefaults()) if (!loaded.Any(x => string.Equals(x.Name, builtIn.Name, StringComparison.OrdinalIgnoreCase))) { loaded.Add(builtIn); changed = true; }
                if (changed) Save(loaded); return loaded.OrderBy(x => x.Name).ToList();
            }
            catch { return CreateDefaults(); }
        }

        public void Upsert(DoorWindowElevationTemplate template)
        {
            if (template == null || string.IsNullOrWhiteSpace(template.Name)) throw new InvalidOperationException("模板名称不能为空。");
            var all = Load();
            var existing = all.FirstOrDefault(x => string.Equals(x.Name, template.Name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing != null && !string.Equals(existing.Id, template.Id, StringComparison.OrdinalIgnoreCase)) all.Remove(existing);
            all.RemoveAll(x => string.Equals(x.Id, template.Id, StringComparison.OrdinalIgnoreCase));
            template.Name = template.Name.Trim(); template.UpdatedAt = DateTime.Now; all.Add(template); Save(all);
        }

        public void Delete(string id)
        {
            var all = Load(); all.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)); Save(all);
        }

        private static void Save(IList<DoorWindowElevationTemplate> templates)
        {
            using (var stream = File.Create(PathName))
                new DataContractJsonSerializer(typeof(List<DoorWindowElevationTemplate>)).WriteObject(stream, templates.OrderBy(x => x.Name).ToList());
        }

        private static List<DoorWindowElevationTemplate> CreateDefaults()
        {
            return new List<DoorWindowElevationTemplate>
            {
                Template("普通单扇窗（固定）", "窗", "单扇", "固定"),
                Template("普通单扇窗（左平开）", "窗", "单扇", "左平开"),
                Template("普通单扇窗（右平开）", "窗", "单扇", "右平开"),
                Template("普通双扇平开窗", "窗", "双扇等分", "双扇平开"),
                Template("普通双扇推拉窗", "窗", "双扇等分", "双向推拉"),
                Template("普通三扇窗", "窗", "三扇等分", "双扇平开"),
                Template("上亮双扇窗", "窗", "上亮", "双扇平开"),
                Template("普通单扇门", "门", "单扇", "左平开"),
                Template("普通双扇门", "门", "双扇等分", "双扇平开"),
                Template("门联窗", "门联窗", "门联窗", "双扇平开"),
                Template("百叶窗", "百叶", "单扇", "百叶")
            };
        }

        private static DoorWindowElevationTemplate Template(string name, string type, string division, string opening)
        {
            return new DoorWindowElevationTemplate { Id = Guid.NewGuid().ToString("N"), Name = name, ElevationType = type, DivisionPreset = division, OpeningMode = opening, InstallationGap = 20d, UpdatedAt = DateTime.Now };
        }
    }
}
