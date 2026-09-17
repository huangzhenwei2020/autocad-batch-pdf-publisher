using System;
using System.Collections.Generic;

namespace WL.Stair.Tests
{
    internal static class TestAssert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    string.Format("{0} Expected: {1}; actual: {2}.", message, expected, actual));
            }
        }

        public static void NearlyEqual(double expected, double actual, double tolerance, string message)
        {
            if (Math.Abs(expected - actual) > tolerance)
            {
                throw new InvalidOperationException(
                    string.Format("{0} Expected: {1}; actual: {2}.", message, expected, actual));
            }
        }
    }
}

