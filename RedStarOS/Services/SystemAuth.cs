// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// Checks the user's own Linux password through PAM (Linux-PAM's libpam, the system's sign-in library), the way
/// screen lockers do: whatever the system's sign-in accepts (the password, and any other PAM step it has set up)
/// unlocks the screen. The PAM service is /etc/pam.d/crimson-banner (install-session.sh adds one that includes the
/// system's "login" rules), else "login" itself. A non-root user's password is checked by the system's setuid helper
/// (unix_chkpwd), so Crimson Banner never reads the password database.
/// </summary>
public static class SystemAuth
{
    public const string Service = "crimson-banner";

    private const int PAM_SUCCESS = 0, PAM_PROMPT_ECHO_OFF = 1, PAM_PROMPT_ECHO_ON = 2, PAM_CONV_ERR = 19;
    private const int PAM_AUTH_ERR = 7, PAM_USER_UNKNOWN = 10, PAM_MAXTRIES = 11, PAM_ACCT_EXPIRED = 13, PAM_PERM_DENIED = 6, PAM_CRED_INSUFFICIENT = 8;

    /// <summary>Right, wrong, or PAM couldn't say (missing, misconfigured): then the caller decides.</summary>
    public enum Outcome { Accepted, Refused, Unavailable }

    [StructLayout(LayoutKind.Sequential)]
    private struct PamMessage { public int Style; public IntPtr Msg; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PamResponse { public IntPtr Resp; public int RetCode; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ConvFn(int count, IntPtr messages, out IntPtr responses, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct PamConv { public IntPtr Conv; public IntPtr Data; }

    [DllImport("libpam.so.0")] private static extern int pam_start(string service, string user, ref PamConv conv, out IntPtr handle);
    [DllImport("libpam.so.0")] private static extern int pam_authenticate(IntPtr handle, int flags);
    [DllImport("libpam.so.0")] private static extern int pam_acct_mgmt(IntPtr handle, int flags);
    [DllImport("libpam.so.0")] private static extern int pam_end(IntPtr handle, int status);
    [DllImport("libpam.so.0")] private static extern IntPtr pam_strerror(IntPtr handle, int err);
    [DllImport("libc")] private static extern IntPtr calloc(UIntPtr n, UIntPtr size);
    [DllImport("libc")] private static extern IntPtr strdup(IntPtr s);

    private static bool? _available;

    /// <summary>Whether libpam can be loaded here.</summary>
    public static bool Available
    {
        get
        {
            if (_available is { } a) return a;
            try { _available = NativeLibrary.TryLoad("libpam.so.0", out var h) && h != IntPtr.Zero; }
            catch { _available = false; }
            return _available.Value;
        }
    }

    /// <summary>The PAM service the lock screen uses.</summary>
    public static string ServiceName => File.Exists("/etc/pam.d/" + Service) || File.Exists("/usr/lib/pam.d/" + Service) ? Service : "login";

    /// <summary>Checks <paramref name="password"/> for the signed-in user off the UI thread (a wrong password takes
    /// PAM a couple of seconds on purpose). Null when it's right, else why not.</summary>
    public static Task<(Outcome Outcome, string? Why)> CheckAsync(string password, string? user = null, string? service = null) =>
        Task.Run(() => Check(password, user ?? Environment.UserName, service ?? ServiceName));

    private static (Outcome, string?) Check(string password, string user, string service)
    {
        if (!Available) return (Outcome.Unavailable, "PAM isn't available");
        var secret = Marshal.StringToCoTaskMemUTF8(password);
        var name = Marshal.StringToCoTaskMemUTF8(user);
        ConvFn conv = (int count, IntPtr messages, out IntPtr responses, IntPtr data) =>
        {
            responses = IntPtr.Zero;
            if (count <= 0 || count > 32) return PAM_CONV_ERR;
            var size = (UIntPtr)Marshal.SizeOf<PamResponse>();
            var arr = calloc((UIntPtr)count, size);          // PAM frees it (and each answer) with free()
            if (arr == IntPtr.Zero) return PAM_CONV_ERR;
            for (int i = 0; i < count; i++)
            {
                // Linux-PAM passes an array of pointers to messages
                var msgPtr = Marshal.ReadIntPtr(messages, i * IntPtr.Size);
                var msg = Marshal.PtrToStructure<PamMessage>(msgPtr);
                IntPtr answer = msg.Style switch
                {
                    PAM_PROMPT_ECHO_OFF => strdup(secret),
                    PAM_PROMPT_ECHO_ON => strdup(name),
                    _ => IntPtr.Zero,                          // information and error messages need no answer
                };
                Marshal.StructureToPtr(new PamResponse { Resp = answer, RetCode = 0 }, arr + i * (int)size, false);
            }
            responses = arr;
            return PAM_SUCCESS;
        };
        var pc = new PamConv { Conv = Marshal.GetFunctionPointerForDelegate(conv), Data = IntPtr.Zero };
        IntPtr h = IntPtr.Zero;
        try
        {
            int r = pam_start(service, user, ref pc, out h);
            if (r != PAM_SUCCESS) return (Outcome.Unavailable, "PAM couldn't start: " + Error(h, r));
            r = pam_authenticate(h, 0);
            if (r == PAM_SUCCESS) r = pam_acct_mgmt(h, 0);
            (Outcome, string?) result = r switch
            {
                PAM_SUCCESS => (Outcome.Accepted, null),
                PAM_AUTH_ERR or PAM_USER_UNKNOWN or PAM_MAXTRIES or PAM_ACCT_EXPIRED or PAM_PERM_DENIED or PAM_CRED_INSUFFICIENT => (Outcome.Refused, Error(h, r)),
                _ => (Outcome.Unavailable, Error(h, r)),        // the service is broken, not the password wrong
            };
            pam_end(h, r);
            h = IntPtr.Zero;
            return result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _available = false;
            return (Outcome.Unavailable, "PAM isn't available");
        }
        finally
        {
            if (h != IntPtr.Zero) pam_end(h, 0);
            // wipe our copy of the password before giving the memory back
            for (int i = 0; Marshal.ReadByte(secret, i) != 0; i++) Marshal.WriteByte(secret, i, 0);
            Marshal.FreeCoTaskMem(secret);
            Marshal.FreeCoTaskMem(name);
            GC.KeepAlive(conv);
        }
    }

    private static string Error(IntPtr h, int code)
    {
        try { return Marshal.PtrToStringUTF8(pam_strerror(h, code)) ?? $"PAM error {code}"; }
        catch { return $"PAM error {code}"; }
    }
}
