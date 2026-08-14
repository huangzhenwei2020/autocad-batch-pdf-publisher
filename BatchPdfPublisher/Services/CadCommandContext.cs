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
            Exception captured = null;
            await Application.DocumentManager.ExecuteInCommandContextAsync(
                unused =>
                {
                    // Never allow a managed exception to cross AutoCAD's native
                    // command-context callback. Some AutoCAD releases terminate
                    // the process with e0434352 instead of faulting the returned
                    // Task. Re-throw only after control has returned to managed UI.
                    try { action(); }
                    catch (Exception exception) { captured = exception; }
                    return Task.FromResult(0);
                },
                null);
            if (captured != null) throw new InvalidOperationException(captured.Message, captured);
        }
#endif
    }
}
