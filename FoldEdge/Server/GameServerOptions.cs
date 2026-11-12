namespace FoldEdge;

/// <summary>
/// SCP:SL dedicated-server process options (bound from the "GameServer" section).
/// </summary>
public sealed class GameServerOptions
{
    /// <summary>Full path to SCPSL.exe or LocalAdmin.exe. Empty = auto-detect.</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>Working directory. Empty = the executable's directory.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Command-line arguments passed to the server.</summary>
    public string Arguments { get; set; } = "-batchmode -nographics -logFile -";

    /// <summary>Start the server when FoldEdge starts.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// If a SCPSL/LocalAdmin process is already running, stop it and relaunch it
    /// as a hidden child. Set false to leave the external process untouched.
    /// </summary>
    public bool TakeoverExisting { get; set; } = true;

    /// <summary>Seconds to wait for a graceful stop before killing the tree.</summary>
    public int GracefulStopSeconds { get; set; } = 8;

    /// <summary>Delay between stop and start on restart.</summary>
    public int RestartDelaySeconds { get; set; } = 3;

    /// <summary>Console command written to stdin before killing (empty = skip).</summary>
    public string StopCommand { get; set; } = "exit";

    /// <summary>Process names used to detect an already-running server.</summary>
    public string[] ProcessNames { get; set; } = new[] { "SCPSL", "LocalAdmin" };
}

public sealed class GameServerStatus
{
    public bool Configured { get; set; }
    public string Executable { get; set; } = string.Empty;
    public bool Running { get; set; }
    public bool Managed { get; set; }
    public int Pid { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public long UptimeSeconds { get; set; }
    public bool AutoStart { get; set; }
    public int? LastExitCode { get; set; }
    public string LastError { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public sealed class GameServerActionResult
{
    public bool Ok { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public GameServerStatus Status { get; set; } = new();
}
