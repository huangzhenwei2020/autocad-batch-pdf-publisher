using System;
using System.Collections.Generic;
using System.Linq;

namespace BatchPdfPublisher.Services
{
    // Coordinates are local scalar stations, never rounded display strings.
    internal static class DimensionChainGeometry
    {
        internal static double[] Stations(IEnumerable<double> input, double epsilon)
        {
            if (!(epsilon > 0) || double.IsInfinity(epsilon)) throw new ArgumentException("无效的尺寸容差。");
            var result = new List<double>();
            foreach (var x in input.OrderBy(x => x))
            {
                if (double.IsNaN(x) || double.IsInfinity(x)) throw new ArgumentException("尺寸必须是有限数值。");
                if (result.Count == 0 || x - result[result.Count - 1] > epsilon) result.Add(x);
            }
            return result.ToArray();
        }
        internal static double MoveStation(double[] stations, int index, double value, double epsilon)
        {
            if (index < 0 || index >= stations.Length || double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("无效的界点位置。");
            if (index > 0 && value < stations[index - 1] - epsilon || index + 1 < stations.Length && value > stations[index + 1] + epsilon)
                throw new ArgumentException("界点不能越过相邻界点；可移到相邻点合并零尺寸。");
            if (index > 0 && Math.Abs(value - stations[index - 1]) <= epsilon) return stations[index - 1];
            if (index + 1 < stations.Length && Math.Abs(value - stations[index + 1]) <= epsilon) return stations[index + 1];
            return value;
        }
        internal static int Nearest(double[] stations, double value)
        {
            if (stations.Length == 0) throw new ArgumentException("尺寸串没有界点。");
            var index = 0;
            for (var i = 1; i < stations.Length; i++) if (Math.Abs(stations[i] - value) < Math.Abs(stations[index] - value)) index = i;
            return index;
        }
    }
}
