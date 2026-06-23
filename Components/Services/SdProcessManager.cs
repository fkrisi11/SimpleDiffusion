using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SimpleDiffusion.Components.Services
{
    public enum SdStatus { Stopped, Starting, Running, Stopping }

    /// <summary>
    /// Owns the single Stable Diffusion server process for the whole application. Registered as a
    /// singleton, so every connected web user shares one instance, one console log, and one status
    /// (rather than each connection spawning its own). UI components subscribe to <see cref="Changed"/>.
    /// </summary>
    public sealed class SdProcessManager : IAsyncDisposable
    {
        private const int MaxLines = 5000;

        private readonly object _logGate = new();
        private readonly List<string> _log = new();
        private readonly System.Threading.Timer _flush;
        private readonly ChildProcessJob? _job;
        private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
        private static readonly Regex ProgressRegex = new(@"\d{1,3}%\|", RegexOptions.Compiled);

        private Process? _proc;
        private volatile bool _dirty;

        public SdStatus Status { get; private set; } = SdStatus.Stopped;

        /// <summary>Raised when the log or status changes. Coalesced to ~4x/sec for log output.</summary>
        public event Action? Changed;

        public SdProcessManager()
        {
            // Coalesce bursty console output into a few UI refreshes per second.
            _flush = new System.Threading.Timer(_ =>
            {
                if (_dirty) { _dirty = false; Notify(); }
            }, null, 250, 250);

            // Job object so the SD process tree is killed whenever THIS app process dies — even on
            // a hard kill / crash, where graceful Dispose never runs. Windows-only.
            try { _job = ChildProcessJob.TryCreate(); } catch { _job = null; }
        }

        private void Notify()
        {
            try { Changed?.Invoke(); } catch { }
        }

        public bool IsRunning
        {
            get
            {
                try { return _proc is { HasExited: false }; }
                catch { return false; }
            }
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_logGate) return _log.ToArray();
        }

        public void Start(string batchPath)
        {
            if (IsRunning) return;

            if (string.IsNullOrWhiteSpace(batchPath) || !File.Exists(batchPath))
            {
                AddLine($"[manager] Batch file not found: {batchPath}");
                return;
            }

            var workingDir = Path.GetDirectoryName(batchPath) ?? Directory.GetCurrentDirectory();
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchPath}\"",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Force Python to flush immediately so startup output (and the server URL) streams live
            // instead of sitting in a block buffer until generation forces a flush.
            psi.Environment["PYTHONUNBUFFERED"] = "1";

            try
            {
                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += (_, e) => AddLine(e.Data);
                proc.ErrorDataReceived += (_, e) => AddLine(e.Data);
                proc.Exited += (_, _) =>
                {
                    AddLine("[manager] Process exited.");
                    SetStatus(SdStatus.Stopped);
                };

                proc.Start();
                _job?.AddProcess(proc); // tie its lifetime to ours
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                _proc = proc;
                AddLine($"[manager] Started: {batchPath}");
                SetStatus(SdStatus.Starting);
            }
            catch (Exception ex)
            {
                AddLine($"[manager] Failed to start: {ex.Message}");
                _proc = null;
                SetStatus(SdStatus.Stopped);
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            SetStatus(SdStatus.Stopping);
            try
            {
                _proc!.Kill(entireProcessTree: true); // SD spawns child processes — kill the whole tree
            }
            catch (Exception ex)
            {
                AddLine($"[manager] Stop error: {ex.Message}");
            }
        }

        public async Task RestartAsync(string batchPath)
        {
            Stop();
            for (int i = 0; i < 60 && IsRunning; i++) await Task.Delay(100); // wait for exit (max ~6s)
            Start(batchPath);
        }

        public void ClearLog()
        {
            lock (_logGate) _log.Clear();
            _dirty = false;
            Notify();
        }

        private void AddLine(string? raw)
        {
            if (raw is null) return;
            var line = AnsiRegex.Replace(raw, "");

            // Carriage-return progress bars split into empty segments; drop them so they don't
            // spam blank lines.
            if (string.IsNullOrWhiteSpace(line)) return;

            var m = ProgressRegex.Match(line);
            bool isProgress = m.Success;
            // Label = the text before the percentage (e.g. "Total progress:" or "" for the bare
            // step bar) so each distinct bar redraws on its own line in place.
            string label = isProgress ? line.Substring(0, m.Index).TrimEnd() : "";

            lock (_logGate)
            {
                int replaceAt = -1;
                if (isProgress)
                {
                    // Scan the trailing run of progress lines for one with the same label. Stop at
                    // the first non-progress line (the previous progress block has ended).
                    for (int i = _log.Count - 1; i >= 0; i--)
                    {
                        var em = ProgressRegex.Match(_log[i]);
                        if (!em.Success) break;
                        if (_log[i].Substring(0, em.Index).TrimEnd() == label) { replaceAt = i; break; }
                    }
                }

                if (replaceAt >= 0)
                {
                    _log[replaceAt] = line; // redraw the same bar in place
                }
                else
                {
                    _log.Add(line);
                    if (_log.Count > MaxLines) _log.RemoveRange(0, _log.Count - MaxLines);
                }
            }

            _dirty = true;
            CheckReady(line);
        }

        private void CheckReady(string line)
        {
            if (Status == SdStatus.Starting &&
                (line.Contains("Running on local URL", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Model loaded", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Startup time:", StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus(SdStatus.Running);
            }
        }

        private void SetStatus(SdStatus s)
        {
            Status = s;
            Notify();
        }

        public async ValueTask DisposeAsync()
        {
            await _flush.DisposeAsync();
            try
            {
                if (IsRunning) _proc!.Kill(entireProcessTree: true);
            }
            catch { }
            _proc?.Dispose();
            _job?.Dispose();
        }
    }

    /// <summary>
    /// A Windows Job Object configured to kill all assigned processes when the job handle closes —
    /// which happens automatically when this app process dies (graceful exit, crash, or hard kill).
    /// </summary>
    internal sealed class ChildProcessJob : IDisposable
    {
        private IntPtr _handle;

        private ChildProcessJob(IntPtr handle) => _handle = handle;

        public static ChildProcessJob? TryCreate()
        {
            if (!OperatingSystem.IsWindows()) return null;

            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return null;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int length = Marshal.SizeOf(info);
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)length))
                {
                    CloseHandle(handle);
                    return null;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }

            return new ChildProcessJob(handle);
        }

        public void AddProcess(Process process)
        {
            if (_handle != IntPtr.Zero)
            {
                try { AssignProcessToJobObject(_handle, process.Handle); } catch { }
            }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
