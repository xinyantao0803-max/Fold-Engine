using System.Text.Json;

namespace FoldEdge;

/// <summary>
/// Time-limited access kill switch. Every few seconds it looks for accounts whose
/// ExpiresAtUtc has passed and, exactly once per account:
///   1. bumps TokenVersion (all JWTs are rejected immediately),
///   2. disables the VPN credential,
///   3. notifies FoldEngine to revoke in-game permissions and restart the round,
///   4. writes a bilingual security warning to the log center.
/// </summary>
public sealed class ExpiryService
{
    private readonly UserStore _store;
    private readonly LogService _log;
    private readonly Func<string, Task<int>> _broadcast;
    private readonly CancellationTokenSource _cts = new();

    public ExpiryService(UserStore store, LogService log, Func<string, Task<int>> broadcast)
    {
        _store = store;
        _log = log;
        _broadcast = broadcast;
    }

    public void Start()
    {
        _ = Task.Run(RunAsync);
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { /* shutdown */ }
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                DateTime now = DateTime.UtcNow;
                foreach (var user in _store.All())
                {
                    if (user.Role == "Owner" || user.RevokedAtUtc != null)
                        continue;
                    if (!user.ExpiresAtUtc.HasValue || user.ExpiresAtUtc.Value > now)
                        continue;

                    if (!_store.TryMarkRevoked(user.Id, out var revoked) || revoked == null)
                        continue;

                    _log.Write("Warn",
                        $"限时权限到期：{revoked.Username} 的 Web 会话与 VPN 凭证已吊销",
                        $"Time-limited access expired: Web session and VPN credential revoked for {revoked.Username}");

                    if (!string.IsNullOrWhiteSpace(revoked.SteamId))
                    {
                        string payload = JsonSerializer.Serialize(new
                        {
                            type = "command",
                            command = "revoke-access",
                            steamId = revoked.SteamId,
                            roundRestart = true
                        });

                        int sent = await _broadcast(payload);
                        _log.Write("Warn",
                            $"已通知 FoldEngine 收回 {revoked.SteamId} 的游戏权限并重启回合（节点 {sent}）",
                            $"FoldEngine notified to revoke in-game access for {revoked.SteamId} and restart the round (nodes {sent})");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Write("Error",
                    $"限时权限检查失败: {ex.Message}",
                    $"Access expiry check failed: {ex.Message}");
            }
        }
    }
}
