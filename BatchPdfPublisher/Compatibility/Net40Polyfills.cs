#if CAD_NET40
namespace System.Runtime.CompilerServices
{
    [System.AttributeUsage(System.AttributeTargets.Parameter, Inherited = false)]
    internal sealed class CallerMemberNameAttribute : System.Attribute
    {
    }
}

namespace System.Linq
{
    internal static class Net40EnumerableExtensions
    {
        public static System.Collections.Generic.HashSet<TSource> ToHashSet<TSource>(
            this System.Collections.Generic.IEnumerable<TSource> source,
            System.Collections.Generic.IEqualityComparer<TSource> comparer)
        {
            return new System.Collections.Generic.HashSet<TSource>(source, comparer);
        }
    }
}
#endif
