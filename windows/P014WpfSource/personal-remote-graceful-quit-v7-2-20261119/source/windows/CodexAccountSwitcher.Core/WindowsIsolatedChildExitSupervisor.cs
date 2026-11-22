using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// P1-B kernel-handle child supervision in an OFFLINE WPF TEST fixture only.
/// This does not attest a Telegram process and is NOT invoked by the live App.
/// OS process/job handles prove local exit only to the current process; a
/// separately authenticated supervisor is required before central settlement.
/// </summary>
internal static class WindowsIsolatedChildExitSupervisor
{
    internal enum FixtureMode { ExitZero, ExitNonzero, SleepFiveSeconds, SpawnSleepingDescendant }
    internal enum Outcome { VerifiedZeroExitAndEmptyJob, NonzeroExit, TimedOutOrJobNotEmpty, StartOrObservationFailed }
    internal sealed record Observation(
        Outcome Result, int ChildPid, uint? ExitCode, uint ActiveJobProcesses,
        DateTimeOffset ObservedUtc)
    {
        internal bool LocalQuiescenceConfirmed =>
            Result is Outcome.VerifiedZeroExitAndEmptyJob or Outcome.NonzeroExit
            && ActiveJobProcesses == 0;
        internal bool ProductionAttestation => false;
        internal bool CanSettleCentralReservation => false;
    }

    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint KillOnJobClose = 0x00002000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint Infinite = 0xffffffff;

    /// <summary>
    /// Launch only the UiChecks fixture exe; no shell, PowerShell, Bot tokens,
    /// command injection, Codex account reads or actual Telegram transport.
    /// </summary>
    internal static Observation ObserveFixture(
        string absoluteUiChecksExecutable, FixtureMode mode, int timeoutMs)
    {
        if (!OperatingSystem.IsWindows() ||
            timeoutMs is < 50 or > 15000 ||
            !Path.IsPathFullyQualified(absoluteUiChecksExecutable) ||
            !string.Equals(Path.GetFileName(absoluteUiChecksExecutable),
                "CodexAccountSwitcher.UiChecks.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(absoluteUiChecksExecutable))
            return Failed();

        var argument = mode switch
        {
            FixtureMode.ExitZero => "exit-zero",
            FixtureMode.ExitNonzero => "exit-nonzero",
            FixtureMode.SleepFiveSeconds => "sleep-five-seconds",
            FixtureMode.SpawnSleepingDescendant => "spawn-descendant",
            _ => ""
        };
        if (argument.Length == 0)
            return Failed();

        IntPtr job = IntPtr.Zero;
        ProcessInformation process = default;
        var start = new StartupInfo { Cb = (uint)Marshal.SizeOf<StartupInfo>() };
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return Failed();

            var limits = new JobObjectExtendedLimitInformation();
            limits.Basic.LimitFlags = KillOnJobClose;
            if (!SetInformationJobObject(job, 9, ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                return Failed();

            // Suspended launch avoids a time-of-check/time-of-use gap:
            // even a very fast child cannot spawn descendants outside our job.
            var command = new StringBuilder(
                "\"" + absoluteUiChecksExecutable.Replace("\"", "\\\"") +
                "\" --isolated-supervisor-fixture " + argument);
            if (!CreateProcess(absoluteUiChecksExecutable, command, IntPtr.Zero,
                IntPtr.Zero, false, CreateSuspended | CreateNoWindow,
                IntPtr.Zero, Path.GetDirectoryName(absoluteUiChecksExecutable),
                ref start, out process))
                return Failed();

            if (!AssignProcessToJobObject(job, process.ProcessHandle))
            {
                // Child is still suspended: terminate without executing it.
                TerminateProcess(process.ProcessHandle, 1);
                WaitForSingleObject(process.ProcessHandle, 2000);
                return Failed();
            }
            if (ResumeThread(process.ThreadHandle) == uint.MaxValue)
            {
                TerminateJobObject(job, 1);
                return Failed();
            }

            var watch = Stopwatch.StartNew();
            var waited = WaitForSingleObject(process.ProcessHandle, (uint)timeoutMs);
            if (waited is not WaitObject0)
            {
                TerminateJobObject(job, 1);
                return new(Outcome.TimedOutOrJobNotEmpty, process.ProcessId,
                    null, JobActiveProcesses(job), DateTimeOffset.UtcNow);
            }

            // A parent can exit before descendants. The job must be empty,
            // not just the initial PID. This also resists PID reuse because
            // we retained the real process HANDLE and own job handle.
            uint remaining = JobActiveProcesses(job);
            while (remaining != 0 && watch.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(20);
                remaining = JobActiveProcesses(job);
            }
            if (remaining != 0)
            {
                TerminateJobObject(job, 1);
                return new(Outcome.TimedOutOrJobNotEmpty, process.ProcessId,
                    null, remaining, DateTimeOffset.UtcNow);
            }

            if (!GetExitCodeProcess(process.ProcessHandle, out var exitCode))
                return Failed();
            return new(exitCode == 0
                ? Outcome.VerifiedZeroExitAndEmptyJob
                : Outcome.NonzeroExit, process.ProcessId, exitCode, 0,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or Win32Exception or NotSupportedException
            or System.Security.SecurityException)
        {
            return Failed();
        }
        finally
        {
            if (process.ThreadHandle != IntPtr.Zero)
                CloseHandle(process.ThreadHandle);
            if (process.ProcessHandle != IntPtr.Zero)
                CloseHandle(process.ProcessHandle);
            // Non-escaping descendants are terminated if the job is still
            // populated. A timeout never produces an affirmative attestation.
            if (job != IntPtr.Zero)
                CloseHandle(job);
        }
    }

    private static Observation Failed() => new(Outcome.StartOrObservationFailed,
        0, null, uint.MaxValue, DateTimeOffset.UtcNow);

    private static uint JobActiveProcesses(IntPtr job)
    {
        if (QueryInformationJobObject(job, 1,
            out JobObjectBasicAccountingInformation accounting,
            (uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>(),
            IntPtr.Zero))
            return accounting.ActiveProcesses;
        return uint.MaxValue; // fail closed on an unreadable job.
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars,
            FillAttribute, Flags;
        public ushort ShowWindow, Reserved2;
        public IntPtr Reserved2Pointer, StandardInput, StandardOutput,
            StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle, ThreadHandle;
        public int ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit,
            PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime,
            ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses,
            ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass,
        ref JobObjectExtendedLimitInformation info, uint infoLength);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName,
        StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string? currentDirectory, ref StartupInfo start,
        out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint waitMs);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
        out JobObjectBasicAccountingInformation info, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
