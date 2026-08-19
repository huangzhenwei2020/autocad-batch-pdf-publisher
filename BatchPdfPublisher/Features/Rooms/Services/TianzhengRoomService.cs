using Autodesk.AutoCAD.DatabaseServices;
using System;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BatchPdfPublisher.Services
{
    internal sealed class TianzhengRoomInfo
    {
        public ObjectId Id { get; set; }
        public string Name { get; set; }
        public string AreaText { get; set; }
        public double? AreaValue { get; set; }
    }

    internal static class TianzhengRoomService
    {
        public const string RoomDxfName = "TCH_SPACE";

        public static bool IsRoom(DBObject value)
        {
            if (value == null) return false;
            try { return string.Equals(value.GetRXClass().DxfName, RoomDxfName, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static TianzhengRoomInfo Read(DBObject room)
        {
            if (!IsRoom(room)) return null;
            var comObject = room.AcadObject;
            var name = Convert.ToString(GetProperty(comObject, "Name"), CultureInfo.CurrentCulture) ?? string.Empty;
            var area = Convert.ToString(GetProperty(comObject, "UseArea"), CultureInfo.CurrentCulture) ?? string.Empty;
            return new TianzhengRoomInfo
            {
                Id = room.ObjectId,
                Name = name.Trim(),
                AreaText = area.Trim(),
                AreaValue = ParseArea(area)
            };
        }

        public static bool Matches(TianzhengRoomInfo room, TianzhengRoomInfo sample)
        {
            if (room == null || sample == null || !string.Equals(room.Name.Trim(), sample.Name.Trim(), StringComparison.Ordinal)) return false;
            if (room.AreaValue.HasValue && sample.AreaValue.HasValue)
                return Math.Abs(room.AreaValue.Value - sample.AreaValue.Value) <= 0.005d;
            return string.Equals(NormalizeArea(room.AreaText), NormalizeArea(sample.AreaText), StringComparison.OrdinalIgnoreCase);
        }

        public static void Rename(DBObject room, string newName)
        {
            if (!IsRoom(room)) throw new InvalidOperationException("所选对象不是天正房间对象。");
            SetProperty(room.AcadObject, "Name", newName);
        }

        private static object GetProperty(object instance, string propertyName)
        {
            if (instance == null) throw new InvalidOperationException("无法取得天正房间的 COM 对象，请确认图纸由天正打开。");
            return instance.GetType().InvokeMember(propertyName, BindingFlags.GetProperty, null, instance, null, CultureInfo.CurrentCulture);
        }

        private static void SetProperty(object instance, string propertyName, object value)
        {
            if (instance == null) throw new InvalidOperationException("无法取得天正房间的 COM 对象，请确认图纸由天正打开。");
            instance.GetType().InvokeMember(propertyName, BindingFlags.SetProperty, null, instance, new[] { value }, CultureInfo.CurrentCulture);
        }

        private static double? ParseArea(string value)
        {
            var match = Regex.Match(value ?? string.Empty, @"[-+]?\d+(?:[\.,]\d+)?");
            if (!match.Success) return null;
            double result;
            var numeric = match.Value.Replace(',', '.');
            return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out result) ? result : (double?)null;
        }

        private static string NormalizeArea(string value)
        {
            return Regex.Replace(value ?? string.Empty, @"\s+", string.Empty).Replace('，', ',').Trim();
        }
    }
}
