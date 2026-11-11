using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Features.Wrappers;
using PlayerRoles;

namespace FoldEngine
{
    /// <summary>播放列表条目：时间轴上的一个事件节点。</summary>
    public struct PlaylistItem
    {
        public float Time;    // 触发时间（秒）
        public string Action; // 动作：SHOW_TEXT / SHOW_IMAGE / PLAY_VIDEO / PLAY_AUDIO / CHANGE_ROLE / SPAWN_ITEM / CONTROL_DOOR / CLEAR
        public string Url;    // CDN 地址（文本动作时作为文本内容）
        public string Raw;    // 原始 JSON 片段，供动作执行器读取 playerId / role / itemId 等扩展参数
        public long Ts;       // FoldEdge 签名时间戳（Unix 毫秒）
        public string Nonce;  // 一次性随机数（客户端防重放）
        public string Sig;    // FoldEdge ECDSA 签名（插件不生成、不校验，只透传）
        public string Payload; // 已签名的媒体负载 JSON（插件原样拼进广播）
        public string Target; // all / role:Scp3114 / player:765611980...
    }

    /// <summary>
    /// PlaylistTransport：.fdll 数据包解析层。
    /// 负责把 manifest.json 与 timeline.json 解析为可播放的列表（纯字符串解析，见 MiniJson）。
    /// </summary>
    public static class PlaylistTransport
    {
        /// <summary>解析 manifest.json，返回模块名（缺失时回退到 id）。</summary>
        public static string ParseManifestName(string manifestJson)
        {
            if (string.IsNullOrEmpty(manifestJson)) return "unknown";
            var name = MiniJson.GetStringValue(manifestJson, "name");
            if (!string.IsNullOrEmpty(name)) return name;
            var id = MiniJson.GetStringValue(manifestJson, "id");
            return string.IsNullOrEmpty(id) ? "unknown" : id;
        }

        /// <summary>解析 manifest.json 的合规标签数组（18+ / GORE / FLASH / POLITICS 等）。</summary>
        public static List<string> ParseTags(string manifestJson)
        {
            return string.IsNullOrEmpty(manifestJson)
                ? new List<string>()
                : MiniJson.ExtractStringArray(manifestJson, "tags");
        }

        /// <summary>解析 timeline.json，返回按时间升序排列的播放列表。</summary>
        public static List<PlaylistItem> ParseTimeline(string timelineJson)
        {
            var items = new List<PlaylistItem>();
            if (string.IsNullOrEmpty(timelineJson)) return items;

            var body = MiniJson.ExtractArrayBody(timelineJson, "entries");
            if (string.IsNullOrEmpty(body))
            {
                // 规范允许 timeline.json 直接是裸数组 [...]，也兼容 { "entries": [...] }
                var trimmed = timelineJson.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]") && trimmed.Length >= 2)
                {
                    body = trimmed.Substring(1, trimmed.Length - 2).Trim();
                }
            }
            if (string.IsNullOrEmpty(body)) return items;

            foreach (var chunk in MiniJson.SplitObjects(body))
            {
                var action = MiniJson.GetStringValue(chunk, "action");
                if (string.IsNullOrEmpty(action)) action = "SHOW_TEXT";
                var url = MiniJson.GetStringValue(chunk, "url") ?? string.Empty;
                // SHOW_TEXT 允许使用 "text" 字段代替 "url"
                if (url.Length == 0) url = MiniJson.GetStringValue(chunk, "text") ?? string.Empty;
                var time = MiniJson.GetNumberValue(chunk, "time", 0f);

                items.Add(new PlaylistItem
                {
                    Time = time,
                    Action = action,
                    Url = url,
                    Raw = chunk,
                    Ts = (long)MiniJson.GetNumberValue(chunk, "ts", 0f),
                    Nonce = MiniJson.GetStringValue(chunk, "nonce") ?? string.Empty,
                    Sig = MiniJson.GetStringValue(chunk, "sig") ?? string.Empty,
                    Payload = MiniJson.GetStringValue(chunk, "payload") ?? string.Empty,
                    Target = MiniJson.GetStringValue(chunk, "target") ?? "all"
                });
            }
            items.Sort((a, b) => a.Time.CompareTo(b.Time));
            return items;
        }
    }

    /// <summary>
    /// Fold 引擎 UI 管理器（纯服务端实现，无任何 Unity 依赖）。
    /// 媒体一律下发 [FOLD_IMG]/[FOLD_VIDEO]/[FOLD_AUDIO]/[FOLD_TEXT] 标记，
    /// 服务端不再生成任何字符画。
    /// </summary>
    public class FoldUIManager
    {
        /// <summary>纯文本/Hint 广播槽：由 FoldCore 注入 LabAPI 的广播实现。</summary>
        public static Action<string, float> HintSink;

        private readonly object _gate = new object();
        private CancellationTokenSource _cts;

        /// <summary>播放指定模块的播放列表。</summary>
        public async Task Play(string module, List<PlaylistItem> items)
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                }
                _cts = new CancellationTokenSource();
                cts = _cts;
            }

            try
            {
                await PlayRoutineAsync(module, items, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Broadcast($"[Fold] 模块 {module} 播放被切换");
            }
            catch (Exception ex)
            {
                LabApi.Features.Console.Logger.Warn(
                    $"[Fold] 模块 {module} 播放异常终止: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>停止当前播放并清理（含服务端扬声器）。</summary>
        public void Stop()
        {
            lock (_gate)
            {
                if (_cts == null) return;
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
            ActionExecutor.StopAll();
        }

        /// <summary>后台时间轴主循环：精确等待到下一个事件，服务端不再输出任何字符画。</summary>
        private async Task PlayRoutineAsync(string module, List<PlaylistItem> items, CancellationToken ct)
        {
            if (items == null || items.Count == 0)
            {
                Broadcast($"[Fold] 模块 {module} 时间轴为空");
                return;
            }

            float elapsed = 0f;
            int index = 0;

            while (index < items.Count)
            {
                ct.ThrowIfCancellationRequested();

                while (index < items.Count && items[index].Time <= elapsed)
                {
                    ExecuteAction(module, items[index]);
                    index++;
                }
                if (index >= items.Count) break;

                float wait = Math.Max(0.05f, items[index].Time - elapsed);
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                elapsed += wait;
            }

            Broadcast($"[Fold] 模块 {module} 播放结束");
        }

        /// <summary>执行单个动作：媒体动作走客户端渲染，游戏动作交给 ActionExecutor。</summary>
        private void ExecuteAction(string module, PlaylistItem item)
        {
            LabApi.Features.Console.Logger.Info(
                $"[Fold] 正在执行动作：{item.Action} (module={module}, t={item.Time:F1}s)");

            try
            {
                switch (item.Action.ToLowerInvariant())
                {
                    case "clear":
                        SendToTargets($"[Fold] {module} 画面已清除", 1.5f, item.Target);
                        break;
                    case "show_image":
                    case "image":
                        // 服务端不再生成字符画：所有图片必须由客户端 [FOLD_IMG] 渲染
                        BroadcastMedia("[FOLD_IMG]", item, "image");
                        break;
                    case "play_video":
                        BroadcastMedia("[FOLD_VIDEO]", item, "video");
                        break;
                    case "play_audio":
                        // 客户端 2D 音频（ogg/mp3）；服务端 SpeakerToy 走 play_audio_server
                        BroadcastMedia("[FOLD_AUDIO]", item, "audio");
                        break;
                    case "show_subtitle":
                    case "subtitle":
                        BroadcastMedia("[FOLD_TEXT]", item, "text");
                        break;
                    case "play_audio_server":
                    case "change_role":
                    case "spawn_item":
                    case "control_door":
                        ActionExecutor.Execute(module, item);
                        break;
                    case "show_text":
                    case "text":
                    default: // 纯文本广播
                        SendToTargets($"[Fold][{item.Time:F0}s] {item.Url}", 1.5f, item.Target);
                        break;
                }
            }
            catch (Exception ex)
            {
                LabApi.Features.Console.Logger.Warn(
                    $"[Fold] {module}: {item.Action} 执行失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 媒体指令：timeline 中已带 FoldEdge 签名时原样拼接透传；
        /// 没有签名（例如 fold 指令现场触发）则请求 FoldEdge 现场签名。
        /// 标记格式：[FOLD_XXX]&lt;tsMs&gt;|&lt;nonce&gt;|&lt;sig&gt;|&lt;payloadJson&gt;
        /// </summary>
        private void BroadcastMedia(string prefix, PlaylistItem item, string kind)
        {
            if (!string.IsNullOrEmpty(item.Sig) && !string.IsNullOrEmpty(item.Payload) &&
                !string.IsNullOrEmpty(item.Nonce))
            {
                SendToTargets(prefix + item.Ts + "|" + item.Nonce + "|" + item.Sig + "|" + item.Payload,
                    8f, item.Target);
                return;
            }

            switch (kind)
            {
                case "image":
                    FoldEdgeClient.Instance.RequestMedia("image", url: item.Url);
                    break;
                case "video":
                    FoldEdgeClient.Instance.RequestMedia("video", url: item.Url);
                    break;
                case "audio":
                    FoldEdgeClient.Instance.RequestMedia("audio", url: item.Url,
                        volume: MiniJson.GetNumberValue(item.Raw, "volume", 1f));
                    break;
                default:
                    FoldEdgeClient.Instance.RequestMedia("text", text: item.Url);
                    break;
            }
        }

        /// <summary>
        /// 按 target 下发：all 走全服广播；role:/player: 用逐玩家 SendBroadcast，
        /// 只有目标玩家会看到 [FOLD_*] 标记并触发客户端渲染。
        /// </summary>
        private void SendToTargets(string message, float duration, string target)
        {
            if (string.IsNullOrEmpty(target) || target.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                Broadcast(message, duration);
                return;
            }

            if (target.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
            {
                var roleText = target.Substring(5).Trim();
                if (!Enum.TryParse(roleText, true, out RoleTypeId role))
                {
                    LabApi.Features.Console.Logger.Warn($"[Fold] 未知 target role: {roleText}");
                    return;
                }

                int sent = 0;
                foreach (var player in Player.List)
                {
                    if (player.Role != role) continue;
                    player.SendBroadcast(message, (ushort)Math.Ceiling(duration),
                        global::Broadcast.BroadcastFlags.Normal, false);
                    sent++;
                }
                LabApi.Features.Console.Logger.Info($"[Fold] 定向广播 role:{role} → {sent} 人");
                return;
            }

            if (target.StartsWith("player:", StringComparison.OrdinalIgnoreCase))
            {
                var rawId = target.Substring(7).Trim();
                var player = FindPlayer(rawId);
                if (player == null)
                {
                    LabApi.Features.Console.Logger.Warn($"[Fold] 定向广播找不到玩家: {rawId}");
                    return;
                }
                player.SendBroadcast(message, (ushort)Math.Ceiling(duration),
                    global::Broadcast.BroadcastFlags.Normal, false);
                LabApi.Features.Console.Logger.Info($"[Fold] 定向广播 player:{rawId} → {player.Nickname}");
                return;
            }

            Broadcast(message, duration);
        }

        private static Player FindPlayer(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return null;

            string userId = playerId.IndexOf('@') >= 0 ? playerId : playerId + "@steam";
            var player = Player.Get(userId);
            if (player != null) return player;

            foreach (var candidate in Player.List)
            {
                if (string.Equals(candidate.Nickname, playerId, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return null;
        }

        /// <summary>原样广播 FoldEdge 签好的媒体标记（服务端绝不修改签名）。</summary>
        public void BroadcastMarker(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return;
            Broadcast(marker, 8f);
        }

        /// <summary>纯文本广播：优先走 HintSink（LabAPI 广播接口）。</summary>
        private void Broadcast(string message, float duration = 1.5f)
        {
            if (HintSink != null)
            {
                HintSink(message, duration);
            }
        }
    }
}
