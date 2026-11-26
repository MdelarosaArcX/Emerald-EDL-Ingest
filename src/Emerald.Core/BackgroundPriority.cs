using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Emerald.Core;

/// <summary>
/// Puts a helper process - a waveform read, an export - behind the recorder and the transmitter
/// for both the processor and the disk.
///
/// On a single spinning disk the masters are written at 15 MB/s; an export reading the masters at
/// full speed, or a waveform read of a whole proxy, takes the head away from the recorder and
/// shows as 100% active time. Windows schedules disk requests by I/O priority, so a helper at
/// very low priority waits for the recorder's writes instead of competing with them.
///
/// Best effort: a process that has already exited, or a priority Windows refuses, is left as it
/// was. Never used for anything that feeds the air.
/// </summary>
public static class BackgroundPriority
{
    /// <summary>Below-normal processor priority and very low disk priority. True if both were set.</summary>
    public static bool Apply(Process process) => Apply(process, VeryLowIo);

    /// <summary>Disk priorities for <see cref="Apply(Process, int)"/>.</summary>
    public const int VeryLowIo = 0, LowIo = 1;

    /// <summary>
    /// Below-normal processor priority and the given disk priority.
    ///
    /// Very low is for work nobody waits on - a waveform read. Something the operator is watching a
    /// progress bar for - an export - takes low: still behind the recorder's writes, but not
    /// starved by them. At very low, an export reading masters off the same disk the recorder was
    /// writing crawled for as long as the recording ran.
    /// </summary>
    public static bool Apply(Process process, int ioPriority)
    {
        bool done = true;

        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            done = false;
        }

        try
        {
            int priority = Math.Clamp(ioPriority, VeryLowIo, LowIo);
            done &= NtSetInformationProcess(process.Handle, ProcessIoPriority, ref priority, sizeof(int)) == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or
                                   EntryPointNotFoundException or DllNotFoundException)
        {
            done = false;
        }

        return done;
    }

    /// <summary>The process's disk priority: 0 very low, 1 low, 2 normal. Null if it cannot be read.</summary>
    public static int? IoPriorityOf(Process process)
    {
        try
        {
            int value = 0;
            return NtQueryInformationProcess(process.Handle, ProcessIoPriority, ref value, sizeof(int), out _) == 0
                ? value
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or
                                   EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private const int ProcessIoPriority = 33;

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref int info, int length, out int returned);
}
