using System.Runtime.InteropServices;

namespace VsMcpBridge;

/// <summary>
/// Owns a single dedicated STA thread that every piece of Visual Studio COM
/// automation runs on.
///
/// Why this matters:
/// <list type="bullet">
///   <item>
///     An <c>IOleMessageFilter</c> is registered <em>per apartment</em>. The MCP
///     server handles tool calls on thread-pool (MTA) threads, so a filter
///     registered on the main thread does nothing for those calls. Running the COM
///     work on a dedicated STA thread that owns the filter is what actually makes
///     busy / retry-later handling work while Visual Studio is building or
///     repainting.
///   </item>
///   <item>
///     Visual Studio's DTE object model is apartment-threaded and not always
///     re-entrant. Serialising every call onto one thread removes a whole class of
///     RPC_E_CALL_REJECTED / marshalling races that a thread pool otherwise hits.
///   </item>
/// </list>
/// </summary>
public static class VsComHost
{
    private const string WedgedMessage =
        "The Visual Studio COM host thread is still executing an earlier call that never returned. " +
        "Visual Studio is most likely blocked on a modal dialog. Dismiss it in Visual Studio, " +
        "or restart this MCP server.";

    private static readonly object Gate = new();
    private static volatile Thread? _thread;
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> Queue = new();

    /// <summary>Set while a timed-out call is still occupying the COM thread.</summary>
    private static volatile bool _wedged;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DispatchMessage(ref Msg msg);

    /// <summary>
    /// PM_REMOVE is required, not PM_NOREMOVE.
    ///
    /// PM_NOREMOVE leaves the message in the queue, so the next PeekMessage returns the
    /// very same message and the drain loop spins forever - which would wedge the COM
    /// thread (and therefore every tool call) permanently.
    /// </summary>
    private const uint PM_REMOVE = 0x0001;

    /// <summary>
    /// Upper bound on how long the between-calls message pump may spend dispatching before it
    /// hands the thread back. A busy Visual Studio can keep producing messages for a long time,
    /// and an unbounded pump would starve the work queue and stall every tool call behind it.
    /// </summary>
    private const int DrainBudgetMs = 2_000;

    private static void EnsureStarted()
    {
        if (_thread != null) return;

        lock (Gate)
        {
            if (_thread != null) return;

            var ready = new ManualResetEventSlim(false);

            var thread = new Thread(() => ThreadMain(Queue, ready))
            {
                IsBackground = true,
                Name = "VsMcpBridge COM (STA)",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            // Publish only after the apartment is actually usable.
            if (!ready.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("Timed out starting the Visual Studio COM host thread.");
            }

            _thread = thread;
        }
    }

    private static void ThreadMain(System.Collections.Concurrent.BlockingCollection<Action> queue, ManualResetEventSlim ready)
    {
        // The message filter must be registered on THIS thread to have any effect.
        using var filter = ComMessageFilter.Register();

        ready.Set();

        foreach (var work in queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            finally
            {
                // Whatever happens, this thread is free again.
                _wedged = false;
                DrainMessageQueue();
            }
        }
    }

    /// <summary>
    /// Pumps anything COM/OLE queued for this apartment so a busy Visual Studio can
    /// deliver its replies and the message filter keeps working.
    /// </summary>
    private static void DrainMessageQueue()
    {
        const uint WM_QUIT = 0x0012;

        try
        {
            long deadline = Environment.TickCount64 + DrainBudgetMs;

            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) break;
                DispatchMessage(ref msg);

                // Stop once the budget is spent so a message flood cannot stall the work queue.
                // Anything still queued is picked up by the next drain.
                if (Environment.TickCount64 >= deadline) break;
            }
        }
        catch
        {
            // A message pump failure must never take the host thread down.
        }
    }

    /// <summary>Default wait for one COM call. Long-running builds pass a larger value.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs <paramref name="work"/> on the Visual Studio COM thread and returns its result.
    /// Any exception thrown by the work is rethrown on the calling thread.
    /// </summary>
    public static T Run<T>(Func<T> work, TimeSpan? timeout = null)
    {
        var wait = timeout ?? DefaultTimeout;

        EnsureStarted();

        if (_wedged)
        {
            // Fail fast and explain, rather than making the caller wait out another
            // full timeout with no idea what is wrong.
            throw new InvalidOperationException(WedgedMessage);
        }

        T result = default!;
        Exception? error = null;

        // Deliberately not disposed via 'using'. On the timeout path below the COM thread still
        // owns this event and calls Set() when (if) the abandoned call ever returns. Disposing it
        // here would make that Set() throw ObjectDisposedException *on the COM thread*, which
        // escapes ThreadMain and kills the host thread - after which every later tool call queues
        // work that never runs and waits out its own ten-minute timeout.
        var done = new ManualResetEventSlim(false);

        Queue.Add(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });

        if (!done.Wait(wait))
        {
            _wedged = true;

            // The call may have finished between Wait timing out and the line above, in which case
            // the COM thread already cleared the flag and we just set it again - with nothing left
            // to clear it. Re-check after a full fence (Set() is itself fenced on the COM side).
            Interlocked.MemoryBarrier();
            if (done.IsSet) _wedged = false;

            throw new TimeoutException(
                $"Timed out after {wait.TotalMinutes:0} minutes waiting for Visual Studio to respond to a COM request. " +
                "Visual Studio may be showing a modal dialog. The abandoned call is still holding the " +
                "COM host thread, so further calls fail fast until it returns or the server is restarted.");
        }

        // The worker has signalled, so it will never touch this event again.
        done.Dispose();

        if (error != null)
        {
            throw new VsInteropException(VsComError.Describe(error), error);
        }

        return result;
    }
}

/// <summary>Wraps a failure that originated inside the Visual Studio COM object model.</summary>
public sealed class VsInteropException : Exception
{
    public VsInteropException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Turns raw COM / automation exceptions into something an agent can act on.</summary>
public static class VsComError
{
    private const int RpcCallRetryLater = unchecked((int)0x8001010A);
    // What a call fails with once the message filter stops retrying a busy callee.
    private const int RpcCallRejected = unchecked((int)0x80010001);
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int ClassNotAvailable = unchecked((int)0x80040154);
    private const int Disconnected = unchecked((int)0x80010108);
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int EFail = unchecked((int)0x80004005);

    public static string Describe(Exception exception)
    {
        for (Exception? e = exception; e != null; e = e.InnerException)
        {
            if (e is COMException com)
            {
                return com.HResult switch
                {
                    RpcCallRetryLater =>
                        "Visual Studio is busy and rejected the call (RPC_E_SERVERCALL_RETRYLATER, 0x8001010A). " +
                        "It is likely mid-build, showing a modal dialog, or still loading. Retry in a few seconds.",
                    RpcCallRejected =>
                        "Visual Studio stayed busy and kept rejecting the call (RPC_E_CALL_REJECTED, 0x80010001; " +
                        "treat like RPC_E_SERVERCALL_RETRYLATER). It is likely mid-build or showing a modal dialog. Retry shortly.",
                    AccessDenied =>
                        "Access was denied while talking to Visual Studio (E_ACCESSDENIED, 0x80070005). " +
                        "If Visual Studio is elevated and this server is not, run them at the same privilege level.",
                    ClassNotAvailable =>
                        "The Visual Studio DTE automation class is not registered " +
                        "(CLASS_E_CLASSNOTAVAILABLE, 0x80040154). Visual Studio may be starting up or partially installed.",
                    Disconnected or RpcServerUnavailable =>
                        $"The Visual Studio instance went away (0x{com.HResult:X8}). " +
                        "It may have closed or restarted; call vs_list_instances and try again.",
                    EFail =>
                        "Visual Studio returned an unspecified error (E_FAIL, 0x80004005) while processing the request.",
                    _ => $"Visual Studio returned COM error 0x{com.HResult:X8}: {com.Message}"
                };
            }

            if (e is InvalidCastException)
            {
                return "The Visual Studio automation object could not be cast to the expected DTE interface. " +
                       "The running Visual Studio may not expose DTE2 automation.";
            }
        }

        if (exception is TimeoutException)
        {
            return "Timed out waiting for Visual Studio to answer. It may be blocked on a modal dialog.";
        }

        return exception.Message;
    }
}
