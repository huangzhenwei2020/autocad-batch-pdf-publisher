using System;
using Autodesk.AutoCAD.ApplicationServices;
#if !ACAD_R19
using System.Threading.Tasks;
#endif

namespace BatchPdfPublisher.Services
{
    internal static class CadCommandContext
    {
#if ACAD_R19
        public static void Execute(Action action)
        {
            if (action == null) throw new ArgumentNullException("action");
            // AutoCAD 2014 does not expose ExecuteInCommandContextAsync.
            // These callers originate from an AutoCAD modal dialog, so they
            // are already on AutoCAD's UI/command thread.
            action();
        }
#else
        public static async Task ExecuteAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException("action");
            await Application.DocumentManager.ExecuteInCommandContextAsync(
                unused =>
                {
                    action();
                    return Task.FromResult(0);
                },
                null);
        }
#endif
    }
}
