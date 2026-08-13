using System;
using System.Collections.Generic;
using System.IO;

namespace BatchPdfPublisher.Services
{
    public sealed class AutoLayerSettings
    {
        public bool Enabled = false;
        public bool ApplyTextStyles = true;
        public string TextLayer;
        public string AttributeLayer;
        public string DimensionLayer;
        public string TextStyle;
        public string AttributeTextStyle;

        public static string SettingsPath { get { return UserDataPaths.SettingsFile("auto-layer.settings"); } }

        public static AutoLayerSettings Load()
        {
            var profile = DraftingStandardService.LoadProfile();
            var settings = new AutoLayerSettings
            {
                TextLayer = profile.Layer(DraftingStandardProfile.AnnotationTextLayerKey).Name,
                AttributeLayer = profile.Layer(DraftingStandardProfile.AnnotationTextLayerKey).Name,
                DimensionLayer = profile.Layer(DraftingStandardProfile.AnnotationDimensionLayerKey).Name,
                TextStyle = profile.Text(DraftingStandardProfile.BodyTextKey).Name,
                AttributeTextStyle = profile.Text(DraftingStandardProfile.AnnotationTextKey).Name
            };
            try
            {
                if (!File.Exists(SettingsPath)) return settings;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(SettingsPath)) { var split = line.IndexOf('='); if (split > 0) values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim(); }
                string value; if (values.TryGetValue("Enabled", out value)) settings.Enabled = value == "1";
                if (values.TryGetValue("ApplyTextStyles", out value)) settings.ApplyTextStyles = value == "1";
                if (values.TryGetValue("TextLayer", out value) && !string.IsNullOrWhiteSpace(value)) settings.TextLayer = value;
                if (values.TryGetValue("AttributeLayer", out value) && !string.IsNullOrWhiteSpace(value)) settings.AttributeLayer = value;
                if (values.TryGetValue("DimensionLayer", out value) && !string.IsNullOrWhiteSpace(value)) settings.DimensionLayer = value;
                if (values.TryGetValue("TextStyle", out value) && !string.IsNullOrWhiteSpace(value)) settings.TextStyle = value;
                if (values.TryGetValue("AttributeTextStyle", out value) && !string.IsNullOrWhiteSpace(value)) settings.AttributeTextStyle = value;
            }
            catch { }
            return settings;
        }

        public void Save()
        {
            File.WriteAllLines(SettingsPath, new[] { "Enabled=" + (Enabled ? "1" : "0"), "ApplyTextStyles=" + (ApplyTextStyles ? "1" : "0"), "TextLayer=" + TextLayer, "AttributeLayer=" + AttributeLayer, "DimensionLayer=" + DimensionLayer, "TextStyle=" + TextStyle, "AttributeTextStyle=" + AttributeTextStyle });
        }
    }
}
