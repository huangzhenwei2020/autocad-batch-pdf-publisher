using System;
using System.IO;
using System.Text;

namespace BatchPdfPublisher.Services
{
    internal sealed class RoomStatisticsFilterSettings
    {
        public int Mode;
        public string Keywords = RoomStatistics.DefaultKeywords;
        private static string PathName { get { return UserDataPaths.SettingsFile("room-statistics-filter.ini"); } }

        public static RoomStatisticsFilterSettings Load()
        {
            var settings = new RoomStatisticsFilterSettings();
            try
            {
                foreach (var line in File.ReadAllLines(PathName, Encoding.UTF8))
                {
                    var separator = line.IndexOf('='); if (separator < 0) continue;
                    var key = line.Substring(0, separator); var value = line.Substring(separator + 1);
                    int mode;
                    if (key == "Mode" && int.TryParse(value, out mode) && mode >= 0 && mode <= 2) settings.Mode = mode;
                    else if (key == "Keywords") settings.Keywords = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                }
            }
            catch { }
            return settings;
        }

        public void Save()
        {
            File.WriteAllLines(PathName, new[] { "Mode=" + Mode, "Keywords=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Keywords ?? "")) }, Encoding.UTF8);
        }
    }
}
