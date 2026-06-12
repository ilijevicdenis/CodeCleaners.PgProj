// EP-VS — a tiny DTE-over-ROT remote control for the launched VS instance. Everything the tests DO
// goes through DTE COM automation (open file, insert text, execute commands, read the Error List):
// COM dispatch targets the specific devenv process and never touches the global input queue, so the
// harness runs UNFOCUSED — the user keeps working (even in their own VS), and the test instance can
// live minimized or on another virtual desktop. FlaUI/UIA is reserved for READ-ONLY checks.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace PgProj.VisualStudio.UiTests;

public sealed class DteRemote
{
    private readonly object _dte;

    private DteRemote(object dte) => _dte = dte;

    public dynamic Dte => _dte;

    /// <summary>
    /// Finds the DTE object for a specific devenv PID in the Running Object Table
    /// (moniker <c>!VisualStudio.DTE.18.0:&lt;pid&gt;</c>; the version prefix is matched loosely so
    /// VS 2022 (17.x) instances work too). Returns null until VS has registered itself.
    /// </summary>
    public static DteRemote? TryAttach(int processId)
    {
        if (GetRunningObjectTable(0, out var rot) != 0 || rot is null) return null;
        try
        {
            rot.EnumRunning(out var enumMoniker);
            if (enumMoniker is null) return null;
            CreateBindCtx(0, out var bindCtx);
            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                string? name = null;
                try { monikers[0].GetDisplayName(bindCtx, null, out name); } catch { }
                if (name is null) continue;
                if (!name.StartsWith("!VisualStudio.DTE.", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(":" + processId, StringComparison.Ordinal)) continue;
                if (rot.GetObject(monikers[0], out var dte) == 0 && dte is not null)
                    return new DteRemote(dte);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs one dynamic DTE interaction with the standard busy-retry: VS rejects COM calls while its
    /// message pump is busy (RPC_E_CALL_REJECTED / RPC_E_SERVERCALL_RETRYLATER), which is routine
    /// during solution load and heavy typing churn — retry until the deadline instead of failing the
    /// test on a busy IDE. The busy COMException is matched ANYWHERE in the exception chain: dynamic
    /// dispatch wraps it (TargetInvocationException / AggregateException), so a direct-type catch
    /// silently never retries.
    /// </summary>
    public T Invoke<T>(Func<dynamic, T> action, int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            try
            {
                return action(Dte);
            }
            catch (Exception ex) when (IsVsBusy(ex) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static bool IsVsBusy(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is COMException com && (uint)com.HResult is 0x80010001 or 0x8001010A) return true;
            if (e is AggregateException agg && agg.InnerExceptions.Any(IsVsBusy)) return true;
        }
        return false;
    }

    public void Invoke(Action<dynamic> action, int timeoutSeconds = 120) =>
        Invoke<object?>(d => { action(d); return null; }, timeoutSeconds);

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
}
