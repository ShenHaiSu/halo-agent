using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Host.Desktop.Tools.Process;

/// <summary>
/// Win32 Job Object (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) 进程树安全回收器。
/// 自 Windows XP/7 起原生支持；关闭作业句柄时内核原子级终止作业下所有子/孙进程。
/// 非 Windows 平台自动安全降级为 No-Op（IsSupported == false）。
/// </summary>
public sealed class JobObjectManager : IDisposable
{
    /// <summary>
    /// 关闭作业时杀灭作业内全部进程。
    /// </summary>
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private static readonly bool _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private IntPtr _jobHandle = IntPtr.Zero;
    private bool _disposed;

    private JobObjectManager(IntPtr jobHandle)
    {
        _jobHandle = jobHandle;
    }

    /// <summary>
    /// 当前平台是否支持 Job Object（仅 Windows）。
    /// </summary>
    public static bool IsSupported => _isWindows;

    /// <summary>
    /// 是否已成功创建内核作业对象。
    /// </summary>
    public bool IsActive => _jobHandle != IntPtr.Zero;

    /// <summary>
    /// 创建内核作业对象并设置 KILL_ON_JOB_CLOSE 限制。
    /// 非 Windows 或创建失败时返回非活动实例（调用方无需分支判断）。
    /// </summary>
    public static JobObjectManager Create()
    {
        if (!_isWindows)
        {
            return new JobObjectManager(IntPtr.Zero);
        }

        try
        {
            IntPtr handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                return new JobObjectManager(IntPtr.Zero);
            }

            var limitInfo = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            bool ok = NativeMethods.SetInformationJobObject(
                handle,
                NativeMethods.JOBOBJECTINFORMATIONCLASS.JobObjectExtendedLimitInformation,
                ref limitInfo,
                (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()
            );

            if (!ok)
            {
                NativeMethods.CloseHandle(handle);
                return new JobObjectManager(IntPtr.Zero);
            }

            return new JobObjectManager(handle);
        }
        catch (Exception)
        {
            // Win7 极端精简内核缺失 API 时安全降级，调用方回退为 process.Kill()
            return new JobObjectManager(IntPtr.Zero);
        }
    }

    /// <summary>
    /// 将已启动的进程纳入作业管控。非活动实例上调用为 No-Op。
    /// 必须在 process.Start() 之后立即调用。
    /// </summary>
    public bool Assign(System.Diagnostics.Process process)
    {
        if (process == null || _jobHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NativeMethods.AssignProcessToJobObject(_jobHandle, process.Handle);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 关闭作业句柄。Windows 内核保证原子级终止作业下所有子孙进程，零残留。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IntPtr handle = _jobHandle;
        _jobHandle = IntPtr.Zero;

        if (handle != IntPtr.Zero)
        {
            try
            {
                NativeMethods.CloseHandle(handle);
            }
            catch (Exception)
            {
                // 忽略退出阶段句柄释放异常
            }
        }
    }

    private static class NativeMethods
    {
        internal enum JOBOBJECTINFORMATIONCLASS
        {
            JobObjectExtendedLimitInformation = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessCount;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public ulong IoReadOperationCount;
            public ulong IoWriteOperationCount;
            public ulong IoOtherOperationCount;
            public ulong IoReadTransferCount;
            public ulong IoWriteTransferCount;
            public ulong IoOtherTransferCount;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            IntPtr hJob,
            JOBOBJECTINFORMATIONCLASS JobObjectInformationClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
            uint cbJobObjectInformationLength
        );

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);
    }
}
