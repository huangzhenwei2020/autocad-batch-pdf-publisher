using System;
using System.Globalization;
using System.Reflection;

namespace WanLuo.CadInterop
{
    // Shared by the stair adapter and the model probe. Only property getters;
    // the caller owns the CAD context and supplies an explicit property list.
    public static class TianzhengReadOnlyAccess
    {
        public static bool TryReadProperty(object source, string name, out object value, out string error)
        {
            value = null;
            error = null;
            if (source == null) { error = "对象未提供 COM 接口"; return false; }
            if (string.IsNullOrWhiteSpace(name)) { error = "属性名为空"; return false; }
            try
            {
                value = source.GetType().InvokeMember(name, BindingFlags.GetProperty,
                    null, source, null, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception exception)
            {
                var actual = exception is TargetInvocationException && exception.InnerException != null
                    ? exception.InnerException : exception;
                error = actual.GetType().Name + ": " + actual.Message;
                if (error.Length > 400) error = error.Substring(0, 400);
                return false;
            }
        }
    }
}
