using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher;

/// <summary>
/// Windows Job Object with KILL_ON_JOB_CLOSE. A supervised dispatch worker
/// waits on STDIN before it can launch a native Codex app-server child.
/// Therefore the parent assigns it to the job BEFORE providing any job input.
/// All descendants inherit job containment (no breakaway limits enabled).
/// </summary>
public sealed class WindowsCodexJobSupervisor : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose=0x2000;
    private readonly IntPtr job;
    private bool closed;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryUsed;
        public IntPtr PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,int infoClass,IntPtr info,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        IntPtr job,IntPtr processHandle);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        IntPtr job,int infoClass,IntPtr value,uint length,out uint written);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public WindowsCodexJobSupervisor()
    {
        if(!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        job=CreateJobObject(IntPtr.Zero,null);
        if(job==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits=new ExtendedLimit();
        limits.BasicLimitInformation.LimitFlags=JobObjectLimitKillOnJobClose;
        var size=Marshal.SizeOf<ExtendedLimit>();
        var ptr=Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits,ptr,false);
            if(!SetInformationJobObject(job,9,ptr,(uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            CloseHandle(job);
            closed=true;
            throw;
        }
        finally{Marshal.FreeHGlobal(ptr);}
    }

    private uint ActiveProcessCount()
    {
        var size=Marshal.SizeOf<BasicAccounting>();
        var ptr=Marshal.AllocHGlobal(size);
        try
        {
            if(!QueryInformationJobObject(job,1,ptr,(uint)size,out var written) ||
               written<(uint)size)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<BasicAccounting>(ptr).ActiveProcesses;
        }
        finally{Marshal.FreeHGlobal(ptr);}
    }

    public sealed record Result(int ExitCode,bool AllJobProcessesTerminated,
        bool OutcomeUncertain,string Stdout);

    public async Task<Result> RunApprovedWorkerAsync(
        string executable,string expectedWorkerSha256,
        string privateStdinJob,TimeSpan timeout,
        CancellationToken cancellationToken=default,
        string? expectedBundleSha256=null)
    {
        if(closed)throw new ObjectDisposedException(nameof(WindowsCodexJobSupervisor));
        if(string.IsNullOrWhiteSpace(executable) ||
           !Path.IsPathFullyQualified(executable) ||
           Path.GetFileName(executable)!="Codex-Existing-Thread-Fenced-Worker.exe" ||
           !Regex.IsMatch(expectedWorkerSha256??"", "^[a-f0-9]{64}$") ||
           privateStdinJob is null || privateStdinJob.Length>10000 ||
           timeout<TimeSpan.FromSeconds(1) || timeout>TimeSpan.FromMinutes(2))
            throw new ArgumentException("Exact supervised worker and bounded input required");
        var file=new FileInfo(executable);
        if(!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Untrusted supervised worker executable");
        // The reviewed directory digest includes the worker's DLL, deps and
        // runtimeconfig. Keep ALL bundle files locked against writes/deletes
        // throughout the Job Object lifetime. Legacy synthetic no-send
        // checks pass no bundle anchor; the production host requires one.
        using var lockedBundle=expectedBundleSha256 is null ? null :
            WindowsReviewedDotNetBundle.Acquire(
                executable,expectedBundleSha256);
        // A fixed filename is not enough to establish worker integrity.
        // Keep the executable non-writable until Process.Start completes.
        using var held=new FileStream(file.FullName,FileMode.Open,
            FileAccess.Read,FileShare.Read);
        if(!CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(held),Convert.FromHexString(expectedWorkerSha256!)))
            throw new InvalidOperationException("Reviewed worker SHA256 mismatch");
        using var worker=new Process
        {
            StartInfo=new ProcessStartInfo(file.FullName)
            {
                UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardInput=true,RedirectStandardOutput=true,
                RedirectStandardError=true,WorkingDirectory=file.DirectoryName!
            }
        };
        worker.StartInfo.ArgumentList.Add("--approved-scheduled-dispatch");
        if(!worker.Start())throw new InvalidOperationException("Worker launch failed");
        held.Dispose();
        try
        {
            // The worker's first operation blocks reading stdin; no Codex
            // child can launch before this job object assignment.
            if(!AssignProcessToJobObject(job,worker.Handle))
            {
                try{worker.Kill(entireProcessTree:true);}catch{}
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Could not contain worker in Windows Job Object");
            }
            // One deadline covers stdin delivery, exit, and pipe drains.
            using var limit=CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                var stdOut=worker.StandardOutput.ReadToEndAsync(limit.Token);
                var stdErr=worker.StandardError.ReadToEndAsync(limit.Token);
                await worker.StandardInput.WriteAsync(
                    privateStdinJob.AsMemory(),limit.Token);
                await worker.StandardInput.FlushAsync(limit.Token);
                worker.StandardInput.Close();
                await worker.WaitForExitAsync(limit.Token);
                var output=await stdOut;
                await stdErr; // Never forward stderr: may contain local details.
                if(output.Length>4096)
                    return new(worker.ExitCode,false,true,"");
                var active=ActiveProcessCount();
                if(active!=0)
                {
                    // The native app-server grandchild may survive parent exit.
                    TerminateJobObject(job,1);
                    return new(worker.ExitCode,false,true,"");
                }
                return new(worker.ExitCode,true,worker.ExitCode!=0,output);
            }
            catch(OperationCanceledException)
            {
                // A timed-out pipe or RPC is outcome-uncertain. Never retry.
                TerminateJobObject(job,1);
                return new(-1,false,true,"");
            }
        }
        finally
        {
            // Closing the job handle kills any remaining descendant.
            // The caller never reuses this job for another scheduled reset.
            Dispose();
        }
    }

    public void Dispose()
    {
        if(closed)return;
        closed=true;
        CloseHandle(job);
    }
}
