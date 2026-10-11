using System.Runtime.InteropServices;

namespace VsMcpBridge;

/// <summary>
/// Implements <c>IOleMessageFilter</c> so that COM calls into Visual Studio survive the
/// periods where the IDE is too busy to service them - most importantly while a build
/// is running or the UI is repainting.
///
/// Visual Studio answers such calls with <c>RPC_E_SERVERCALL_RETRYLATER</c> (0x8001010A)
/// or "busy". Without a filter the call simply fails immediately.
///
/// The filter is only effective when it is registered on the thread that actually makes
/// the COM call, which is why all DTE work is funnelled through <see cref="VsComHost"/>'s
/// dedicated STA thread.
/// </summary>
public sealed class ComMessageFilter : IDisposable
{
    /// <summary>How long to keep retrying a rejected call before giving up.</summary>
    private const int RetryBudgetMs = 60_000;

    /// <summary>Delay before a retry; grows in steps as the wait drags on.</summary>
    private const int InitialRetryDelayMs = 100;

    private const int MaxRetryDelayMs = 1_000;

    // SERVERCALL_RETRYLATER. (SERVERCALL_REJECTED = 1 is a definite "no" and is not retried.)
    private const int RejectRetryLater = 2;

    /// <summary>
    /// The reply <c>MessagePending</c> must give COM so that it keeps waiting for the callee and
    /// keeps dispatching pending messages (PENDINGMSG_WAITDEFPROCESS).
    ///
    /// Caution - <c>MessagePending</c> is handed a PENDINGTYPE (PENDINGTYPE_TOPLEVEL = 1,
    /// PENDINGTYPE_NESTED = 2) and must answer with a PENDINGMSG (PENDINGMSG_CANCELCALL = 0,
    /// PENDINGMSG_WAITNOPROCESS = 1, PENDINGMSG_WAITDEFPROCESS = 2). The two enumerations are not
    /// interchangeable: deriving the reply from the incoming value makes the common top-level case
    /// answer 0, which cancels the caller's automation call outright.
    /// </summary>
    private const int PendingMsgWaitDefProcess = 2;

    private readonly IMessageFilter? _oldFilter;
    private int _disposed;

    private ComMessageFilter()
    {
        // Must be called from the apartment that will make the COM calls.
        CoRegisterMessageFilter(new MessageFilterImpl(), out _oldFilter);
    }

    /// <summary>Registers a new filter on the calling thread and returns it.</summary>
    public static ComMessageFilter Register() => new();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            CoRegisterMessageFilter(_oldFilter, out _);
        }
        catch
        {
            // The apartment may already be gone during shutdown.
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMessageFilter(IMessageFilter? newFilter, out IMessageFilter? oldFilter);

    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int dwCallType, IntPtr htaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(IntPtr htaskCallee, int dwTickCount, int dwRejectType);

        [PreserveSig]
        int MessagePending(IntPtr htaskCallee, int dwTickCount, int dwPendingType);
    }

    private sealed class MessageFilterImpl : IMessageFilter
    {
        /// <summary>SERVERCALL_ISHANDLED - we do not want to handle incoming calls.</summary>
        public int HandleInComingCall(int dwCallType, IntPtr htaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
            => 0; // SERVERCALL_ISHANDLED

        /// <summary>
        /// Visual Studio rejected the call. Retry with a short backoff while the call is
        /// still inside its budget; give up once the budget is exhausted.
        /// </summary>
        public int RetryRejectedCall(IntPtr htaskCallee, int dwTickCount, int dwRejectType)
        {
            if (dwRejectType != RejectRetryLater)
            {
                return -1; // Cancel the call.
            }

            if (dwTickCount >= RetryBudgetMs)
            {
                return -1; // Out of patience.
            }

            // dwTickCount is the elapsed time so far; grow the delay as it climbs so a
            // long build does not turn into a busy-wait storm.
            int delay = InitialRetryDelayMs;
            if (dwTickCount > 2_000) delay = 250;
            if (dwTickCount > 10_000) delay = MaxRetryDelayMs;

            return delay;
        }

        /// <summary>
        /// Tells COM how to proceed while the callee is blocked.
        ///
        /// Always PENDINGMSG_WAITDEFPROCESS: keep waiting for Visual Studio and let COM dispatch
        /// pending messages, which is what allows a busy IDE to finish and answer. This is
        /// deliberately independent of <paramref name="dwPendingType"/> - that parameter is a
        /// PENDINGTYPE (top-level or nested), not a PENDINGMSG, and must never be echoed back.
        /// </summary>
        public int MessagePending(IntPtr htaskCallee, int dwTickCount, int dwPendingType)
            => PendingMsgWaitDefProcess;
    }
}
