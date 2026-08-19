using System;
using Autodesk.AutoCAD.ApplicationServices;
using System.Threading.Tasks;

namespace BatchPdfPublisher.Services
{
    internal static class CadCommandContext
    {
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
    }
}
