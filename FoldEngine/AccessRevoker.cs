using System;
using LabApi.Features.Console;
using LabApi.Features.Permissions;
using LabApi.Features.Wrappers;

namespace FoldEngine
{
    /// <summary>
    /// 限时权限到期的游戏内执行端：
    ///   1. 按 SteamID 找到在线玩家并移除其全部显式权限；
    ///   2. 按 FoldEdge 指令执行回合重启（RoundRestart）。
    /// 所有调用均由 FoldCore.RunOnMain 切回 Unity 主线程后执行。
    /// </summary>
    internal static class AccessRevoker
    {
        public static void Revoke(string steamId, bool roundRestart)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(steamId))
                {
                    var player = ActionExecutor.FindPlayer(steamId);
                    if (player != null)
                    {
                        var permissions = player.GetPermissions();
                        if (permissions != null && permissions.Length > 0)
                            player.RemovePermissions(permissions);

                        Logger.Warn($"[Fold] 已收回游戏内权限: {player.Nickname} ({steamId})");
                    }
                    else
                    {
                        Logger.Warn($"[Fold] 玩家不在线，跳过游戏内权限收回: {steamId}");
                    }
                }

                if (roundRestart)
                {
                    Logger.Warn("[Fold] 限时权限到期：执行回合重启");
                    Round.RestartSilently();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Fold] revoke-access 执行失败: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
