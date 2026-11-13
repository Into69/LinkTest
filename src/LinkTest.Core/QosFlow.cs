using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LinkTest.Core;

/// <summary>
/// DSCP marking through the Windows qWAVE API. Windows ignores IP_TOS from applications, so this is the
/// supported way; setting an explicit DSCP value requires the process to run as Administrator.
/// </summary>
public sealed class QosFlow : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct QOS_VERSION { public ushort Major; public ushort Minor; }

    [DllImport("qwave.dll", SetLastError = true)]
    static extern bool QOSCreateHandle(ref QOS_VERSION version, out IntPtr handle);
    [DllImport("qwave.dll", SetLastError = true)]
    static extern bool QOSCloseHandle(IntPtr handle);
    [DllImport("qwave.dll", SetLastError = true)]
    static extern bool QOSAddSocketToFlow(IntPtr handle, IntPtr socket, byte[]? dest, int trafficType, uint flags, ref uint flowId);
    [DllImport("qwave.dll", SetLastError = true)]
    static extern bool QOSSetFlow(IntPtr handle, uint flowId, int operation, uint size, ref uint buffer, uint flags, IntPtr overlapped);

    const int QOSTrafficTypeBestEffort = 0;
    const uint QOS_NON_ADAPTIVE_FLOW = 2;
    const int QOSSetOutgoingDSCPValue = 2;

    IntPtr _handle;

    QosFlow(IntPtr h) => _handle = h;

    public static QosFlow? TryCreate(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows()) { error = "DSCP marking is only implemented on Windows"; return null; }
        try
        {
            var v = new QOS_VERSION { Major = 1, Minor = 0 };
            if (QOSCreateHandle(ref v, out var h)) return new QosFlow(h);
            error = $"QOSCreateHandle failed (error {Marshal.GetLastWin32Error()})";
        }
        catch (Exception e) { error = "qWAVE unavailable: " + e.Message; }
        return null;
    }

    /// <summary>Mark traffic from <paramref name="s"/> (to <paramref name="dest"/> for unconnected UDP).</summary>
    public bool TryAdd(Socket s, IPEndPoint? dest, int dscp, out string? error)
    {
        error = null;
        try
        {
            byte[]? sa = null;
            if (dest != null)
            {
                var a = dest.Serialize();
                sa = new byte[a.Size];
                for (int i = 0; i < a.Size; i++) sa[i] = a[i];
            }
            uint flowId = 0;
            if (!QOSAddSocketToFlow(_handle, s.Handle, sa, QOSTrafficTypeBestEffort, QOS_NON_ADAPTIVE_FLOW, ref flowId))
            {
                int e = Marshal.GetLastWin32Error();
                error = e == 1168
                    ? "Windows can't DSCP-mark traffic to this computer itself (loopback); marking works between two machines"
                    : $"QOSAddSocketToFlow failed (error {e})";
                return false;
            }
            uint value = (uint)dscp;
            if (!QOSSetFlow(_handle, flowId, QOSSetOutgoingDSCPValue, sizeof(uint), ref value, 0, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                error = $"setting DSCP {dscp} failed (error {err})" + (err == 5 ? " - run as Administrator" : "");
                return false;
            }
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            try { QOSCloseHandle(_handle); } catch { }
            _handle = IntPtr.Zero;
        }
    }
}
