using System.Runtime.InteropServices;

namespace VsMcpBridge;

/// <summary>
/// Reports the Windows integrity level this process is running at.
///
/// This exists because of a failure mode that is otherwise completely invisible from inside the
/// process. Windows refuses to let a process running below Medium integrity see COM objects -
/// including Running Object Table entries - that were created by a Medium-integrity process.
/// Visual Studio runs at Medium, so a Low-integrity MCP server enumerates an <em>empty</em> ROT
/// with no error whatsoever, and every tool then reports "no running Visual Studio instance".
///
/// The usual cause is a mandatory integrity label on the folder the server was published to: if
/// the executable is labelled Low (for example because it lives in a sandboxed workspace whose
/// directory carries an inheritable Low label), every process started from it inherits Low, even
/// though the user's token is Medium. Detecting that here turns a baffling silent failure into an
/// actionable one.
/// </summary>
public static class ProcessIntegrity
{
    private const int TokenIntegrityLevel = 25;
    private const uint TokenQuery = 0x0008;

    /// <summary>SECURITY_MANDATORY_MEDIUM_RID - the level Visual Studio runs at.</summary>
    private const uint MediumRid = 0x2000;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// The current process's integrity RID (for example 0x1000 for Low, 0x2000 for Medium), or
    /// <c>null</c> when it cannot be determined.
    /// </summary>
    public static uint? GetIntegrityRid()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token) || token == IntPtr.Zero)
            {
                return null;
            }

            // First call sizes the buffer; the documented failure here is expected.
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out uint needed);
            if (needed == 0) return null;

            buffer = Marshal.AllocHGlobal((int)needed);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
            {
                return null;
            }

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero) return null;

            byte subAuthorityCount = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (subAuthorityCount == 0) return null;

            // The integrity level is the last sub-authority of the SID.
            IntPtr ridPointer = GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1));
            return ridPointer == IntPtr.Zero ? null : (uint)Marshal.ReadInt32(ridPointer);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>
    /// True when this process runs below Medium integrity, i.e. below the level Visual Studio's
    /// DTE registration is visible from.
    /// </summary>
    public static bool IsBelowMedium()
        => GetIntegrityRid() is uint rid && rid < MediumRid;

    /// <summary>A human-readable name for the current integrity level.</summary>
    public static string Describe() => GetIntegrityRid() switch
    {
        null => "unknown",
        0x0000 => "Untrusted",
        0x1000 => "Low",
        0x2000 => "Medium",
        0x3000 => "High",
        0x4000 => "System",
        uint rid => $"0x{rid:X}",
    };
}
