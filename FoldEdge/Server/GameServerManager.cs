using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FoldEdge;

/// <summary>
/// Full lifecycle manager for the SCP:SL dedicated server.
///
/// Design notes:
///   - The child is started with UseShellExecute=false, CreateNoWindow=true,
///     WindowStyle=Hidden, so no console window appears.
///   - stdout/stderr are redirected and mirrored into the unified log center,
///     which feeds the Web dashboard.
///   - On Windows the child is assigned to a Job Object with
///     JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. If FoldEdge exits or crashes, closing
///     the job handle kills the server and any grandchildren, so no orphan
///     process is left behind.
///   - A pre-existing SCPSL/LocalAdmin process cannot be adopted; when
///     TakeoverExisting is true it is closed gracefully (then killed) and a new
///     hidden child is started. When false it is reported as an external server
///     and can still be stopped from the dashboard.
/// </summary>
public sealed class GameServerManager : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private readonly GameServerOptions _options;
    private readonly LogService _log;
    private readonly string _contentRoot;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _operation = new(1, 1);

    private Process? _process;
    private Process? _external;
    private DateTime? _startedAtUtc;
    private int? _lastExitCode;
    private string _lastError = string.Empty;
    private string _resolvedExecutable = string.Empty;
    private IntPtr _jobHandle = IntPtr.Zero;
    private bool _disposed;

    public GameServerManager(GameServerOptions options, LogService log, string contentRoot)
    {
        _options = options ?? new GameServerOptions();
        _log = log;
        _contentRoot = contentRoot;
        _resolvedExecutable = ResolveExecutable() ?? string.Empty;
    }

    public GameServerStatus GetStatus()
    {
        lock (_gate)
        {
            bool managed = IsAlive(_process);
            if (!managed && _process != null)
            {
                _process = null;
                _startedAtUtc = null;
            }

            if (_external != null && !IsAlive(_external))
                _external = null;

            bool external = !managed && IsAlive(_external);
            Process? active = managed ? _process : external ? _external : null;

            return new GameServerStatus
            {
                Configured = !string.IsNullOrEmpty(_resolvedExecutable),
                Executable = _resolvedExecutable,
                Running = managed || external,
                Managed = managed,
                Pid = active?.Id ?? 0,
                StartedAtUtc = _startedAtUtc,
                UptimeSeconds = _startedAtUtc.HasValue
                    ? (long)(DateTime.UtcNow - _startedAtUtc.Value).TotalSeconds
                    : 0,
                AutoStart = _options.AutoStart,
                LastExitCode = _lastExitCode,
                LastError = _lastError,
                Source = managed ? "managed" : external ? "external" : string.Empty
            };
        }
    }

    /// <summary>Starts the server on FoldEdge boot when AutoStart is enabled.</summary>
    public void AutoStartIfConfigured()
    {
        if (!_options.AutoStart)
        {
            _log.Write("Info", "[SCPSL] 自动启动已关闭（GameServer:AutoStart=false）",
                "[SCPSL] Auto-start disabled (GameServer:AutoStart=false)");
            return;
        }

        if (string.IsNullOrEmpty(_resolvedExecutable))
        {
            _log.Write("Warn",
                "[SCPSL] 未找到 SCPSL.exe / LocalAdmin.exe；请在 appsettings.json 的 GameServer:ExecutablePath 指定路径",
                "[SCPSL] SCPSL.exe / LocalAdmin.exe not found; set GameServer:ExecutablePath in appsettings.json");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await StartAsync("auto-start");
            }
            catch (Exception ex)
            {
                _log.Write("Error",
                    $"[SCPSL] 自动启动失败: {ex.Message}",
                    $"[SCPSL] Auto-start failed: {ex.Message}");
            }
        });
    }

    public async Task<GameServerActionResult> StartAsync(string actor)
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StartInternalAsync(actor).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task<GameServerActionResult> StopAsync(string actor)
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StopInternalAsync(actor).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task<GameServerActionResult> RestartAsync(string actor)
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopInternalAsync(actor).ConfigureAwait(false);
            int delay = Math.Max(0, _options.RestartDelaySeconds);
            if (delay > 0)
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);

            return await StartInternalAsync(actor).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<GameServerActionResult> StartInternalAsync(string actor)
    {
        lock (_gate)
        {
            if (IsAlive(_process))
            {
                return Result(false, "start", "already_running", GetStatus());
            }
        }

        Process? existing = FindExistingProcess();
        if (existing != null)
        {
            if (!_options.TakeoverExisting)
            {
                lock (_gate) { _external = existing; }
                _log.Write("Warn",
                    $"[SCPSL] 检测到外部服务器进程 pid={existing.Id}，未启用接管；状态标记为 Running(external)",
                    $"[SCPSL] External server process pid={existing.Id} detected; takeover disabled, reporting Running(external)");

                return Result(false, "start", "external_running", GetStatus());
            }

            _log.Write("Warn",
                $"[SCPSL] 检测到已存在的服务器进程 pid={existing.Id}，正在接管：先优雅关闭，再静默重启",
                $"[SCPSL] Existing server pid={existing.Id} detected; taking over: graceful stop, then hidden relaunch");
            await StopProcessAsync(existing, "takeover").ConfigureAwait(false);
        }

        string? executable = ResolveExecutable();
        if (executable == null)
        {
            _lastError = "executable not found";
            _log.Write("Error",
                "[SCPSL] 启动失败：找不到 SCPSL.exe / LocalAdmin.exe。请配置 GameServer:ExecutablePath",
                "[SCPSL] Start failed: SCPSL.exe / LocalAdmin.exe not found. Configure GameServer:ExecutablePath");
            return Result(false, "start", "executable_not_found", GetStatus());
        }

        try
        {
            _resolvedExecutable = executable;
            string workingDirectory = ResolveWorkingDirectory(executable);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = _options.Arguments ?? string.Empty,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += (_, e) => HandleOutput(e.Data, false);
            process.ErrorDataReceived += (_, e) => HandleOutput(e.Data, true);
            process.Exited += (_, _) => HandleExit(process);

            if (!process.Start())
            {
                process.Dispose();
                _lastError = "process start returned false";
                return Result(false, "start", "start_failed", GetStatus());
            }

            lock (_gate)
            {
                _process = process;
                _startedAtUtc = DateTime.UtcNow;
                _lastExitCode = null;
                _lastError = string.Empty;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AssignToJobObject(process);

            _log.Write("Info",
                $"[SCPSL] 已由 {actor} 静默启动 pid={process.Id} exe={executable}",
                $"[SCPSL] Hidden server started by {actor}: pid={process.Id} exe={executable}");
            if (!string.IsNullOrWhiteSpace(_options.Arguments))
            {
                _log.Write("Info",
                    $"[SCPSL] 参数: {_options.Arguments}",
                    $"[SCPSL] Arguments: {_options.Arguments}");
            }

            return Result(true, "start", "started", GetStatus());
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _log.Write("Error",
                $"[SCPSL] 启动失败: {ex.GetType().Name}: {ex.Message}",
                $"[SCPSL] Start failed: {ex.GetType().Name}: {ex.Message}");
            return Result(false, "start", ex.Message, GetStatus());
        }
    }

    private async Task<GameServerActionResult> StopInternalAsync(string actor)
    {
        Process? managed;
        Process? external;
        lock (_gate)
        {
            managed = IsAlive(_process) ? _process : null;
            external = IsAlive(_external) ? _external : null;
        }

        if (managed != null)
        {
            await StopProcessAsync(managed, "stop").ConfigureAwait(false);
            _log.Write("Info",
                $"[SCPSL] 已由 {actor} 停止服务器",
                $"[SCPSL] Server stopped by {actor}");
            return Result(true, "stop", "stopped", GetStatus());
        }

        if (external != null)
        {
            await StopProcessAsync(external, "stop-external").ConfigureAwait(false);
            _log.Write("Warn",
                $"[SCPSL] 已由 {actor} 停止外部服务器进程",
                $"[SCPSL] External server process stopped by {actor}");
            return Result(true, "stop", "stopped_external", GetStatus());
        }

        return Result(true, "stop", "not_running", GetStatus());
    }

    private async Task StopProcessAsync(Process process, string reason)
    {
        try
        {
            if (process.HasExited)
                return;

            if (reason == "takeover" || reason == "stop-external")
            {
                try { process.CloseMainWindow(); } catch { /* hidden/no window */ }
            }
            else
            {
                // LocalAdmin reads the console buffer directly; writing to a
                // redirected stdin makes its reader fail with an IOException.
                // For LocalAdmin we skip stdin and stop the tree after the
                // graceful window instead.
                bool localAdmin = Path.GetFileName(process.StartInfo.FileName)
                    .Equals("LocalAdmin.exe", StringComparison.OrdinalIgnoreCase);
                if (!localAdmin)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(_options.StopCommand))
                        {
                            process.StandardInput.WriteLine(_options.StopCommand);
                            process.StandardInput.Flush();
                        }
                    }
                    catch
                    {
                        // stdin may already be closed
                    }
                }
                else
                {
                    try { process.CloseMainWindow(); } catch { /* no window */ }
                }
            }

            int gracefulMs = Math.Max(1, _options.GracefulStopSeconds) * 1000;
            bool exited = await Task.Run(() => process.WaitForExit(gracefulMs)).ConfigureAwait(false);
            if (!exited)
            {
                _log.Write("Warn",
                    $"[SCPSL] 优雅停止超时（{_options.GracefulStopSeconds}s），强制结束进程树 pid={process.Id}",
                    $"[SCPSL] Graceful stop timed out ({_options.GracefulStopSeconds}s); killing process tree pid={process.Id}");
                try { process.Kill(true); } catch { /* already gone */ }
                await Task.Run(() => process.WaitForExit(5000)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Write("Warn",
                $"[SCPSL] 停止进程时出错: {ex.GetType().Name}: {ex.Message}",
                $"[SCPSL] Error while stopping process: {ex.GetType().Name}: {ex.Message}");
            try { process.Kill(true); } catch { /* ignore */ }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _startedAtUtc = null;
                }

                if (ReferenceEquals(_external, process))
                    _external = null;
            }

            CloseJobObject();
        }
    }

    private void HandleOutput(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        string level = isError ? "Warn" : "Info";
        if (line.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0 ||
            line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
            line.IndexOf("fatal", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            level = "Error";
        }
        else if (line.IndexOf("warn", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            level = "Warn";
        }

        _log.Write(level, "[SCPSL] " + line, "[SCPSL] " + line);
    }

    private void HandleExit(Process process)
    {
        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { /* ignore */ }

        lock (_gate)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
                _startedAtUtc = null;
                _lastExitCode = exitCode;
            }

            if (ReferenceEquals(_external, process))
                _external = null;
        }

        CloseJobObject();
        _log.Write(exitCode == 0 ? "Info" : "Warn",
            $"[SCPSL] 服务器进程已退出 code={(exitCode.HasValue ? exitCode.Value.ToString() : "?")}",
            $"[SCPSL] Server process exited code={(exitCode.HasValue ? exitCode.Value.ToString() : "?")}");
    }

    private Process? FindExistingProcess()
    {
        foreach (string name in _options.ProcessNames ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            Process[] candidates;
            try { candidates = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (Process candidate in candidates)
            {
                try
                {
                    if (candidate.Id == Environment.ProcessId || candidate.HasExited)
                        continue;

                    string path = string.Empty;
                    try { path = candidate.MainModule?.FileName ?? string.Empty; } catch { /* access denied */ }

                    bool sameExecutable = !string.IsNullOrEmpty(_resolvedExecutable) &&
                                          !string.IsNullOrEmpty(path) &&
                                          string.Equals(Path.GetFullPath(path), Path.GetFullPath(_resolvedExecutable),
                                              StringComparison.OrdinalIgnoreCase);
                    bool scpServer = path.Contains("SCP Secret Laboratory", StringComparison.OrdinalIgnoreCase) ||
                                     path.Contains("SCPSL", StringComparison.OrdinalIgnoreCase) ||
                                     name.Equals("SCPSL", StringComparison.OrdinalIgnoreCase) ||
                                     name.Equals("LocalAdmin", StringComparison.OrdinalIgnoreCase);

                    if (sameExecutable || scpServer)
                        return candidate;
                }
                catch
                {
                    // ignore inaccessible processes
                }
            }
        }

        return null;
    }

    private string? ResolveExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_options.ExecutablePath))
        {
            string expanded = Environment.ExpandEnvironmentVariables(_options.ExecutablePath.Trim());
            if (File.Exists(expanded))
                return Path.GetFullPath(expanded);
        }

        string? environment = Environment.GetEnvironmentVariable("SCPSL_EXE");
        if (!string.IsNullOrWhiteSpace(environment) && File.Exists(environment))
            return Path.GetFullPath(environment);

        var candidates = new List<string>
        {
            Path.Combine(_contentRoot, "LocalAdmin.exe"),
            Path.Combine(_contentRoot, "SCPSL.exe"),
            Path.GetFullPath(Path.Combine(_contentRoot, "..", "LocalAdmin.exe")),
            Path.GetFullPath(Path.Combine(_contentRoot, "..", "SCPSL.exe"))
        };

        string? root = Path.GetPathRoot(_contentRoot);
        if (!string.IsNullOrEmpty(root))
        {
            candidates.Add(Path.Combine(root, "SCP Secret Laboratory Dedicated Server", "LocalAdmin.exe"));
            candidates.Add(Path.Combine(root, "SCP Secret Laboratory Dedicated Server", "SCPSL.exe"));
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private string ResolveWorkingDirectory(string executable)
    {
        if (!string.IsNullOrWhiteSpace(_options.WorkingDirectory))
        {
            string expanded = Environment.ExpandEnvironmentVariables(_options.WorkingDirectory.Trim());
            if (Directory.Exists(expanded))
                return expanded;
        }

        return Path.GetDirectoryName(executable) ?? _contentRoot;
    }

    private void AssignToJobObject(Process process)
    {
        if (!OperatingSystem.IsWindows())
            return;

        IntPtr job = IntPtr.Zero;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return;

            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass,
                    ref information, Marshal.SizeOf<JobObjectExtendedLimitInformation>()) ||
                !AssignProcessToJobObject(job, process.Handle))
            {
                CloseHandle(job);
                _log.Write("Warn",
                    "[SCPSL] 无法将进程加入 Job Object；将依赖 FoldEdge 退出时的强制清理",
                    "[SCPSL] Could not assign process to a Job Object; relying on shutdown cleanup");
                return;
            }

            lock (_gate) { _jobHandle = job; }
        }
        catch (Exception ex)
        {
            if (job != IntPtr.Zero)
                CloseHandle(job);
            _log.Write("Warn",
                $"[SCPSL] Job Object 初始化失败: {ex.Message}",
                $"[SCPSL] Job Object setup failed: {ex.Message}");
        }
    }

    private void CloseJobObject()
    {
        IntPtr handle;
        lock (_gate)
        {
            handle = _jobHandle;
            _jobHandle = IntPtr.Zero;
        }

        if (handle != IntPtr.Zero)
        {
            try { CloseHandle(handle); } catch { /* ignore */ }
        }
    }

    private static bool IsAlive(Process? process)
    {
        if (process == null)
            return false;

        try { return !process.HasExited; }
        catch { return false; }
    }

    private GameServerActionResult Result(bool ok, string action, string message, GameServerStatus status)
    {
        return new GameServerActionResult
        {
            Ok = ok,
            Action = action,
            Message = message,
            Status = status
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            Task stop = StopAsync("shutdown");
            stop.Wait(TimeSpan.FromSeconds(Math.Max(2, _options.GracefulStopSeconds) + 6));
        }
        catch
        {
            // best effort
        }

        CloseJobObject();

        Process? external;
        lock (_gate) { external = _external; }
        if (external != null)
        {
            try { if (!external.HasExited) external.Kill(true); } catch { /* ignore */ }
        }
    }

    // ---------------- Win32 Job Object ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
