using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using InventorySystem.Items;
using Interactables.Interobjects.DoorUtils;
using LabApi.Features.Console;
using LabApi.Features.Enums;
using LabApi.Features.Wrappers;
using PlayerRoles;

namespace FoldEngine
{
    /// <summary>
    /// 非媒体类动作执行器：全部使用 LabAPI 原生包装器。
    /// 签名依据 LabAPI 1.1.7 反射结果：
    ///   Player.SetRole(RoleTypeId, RoleChangeReason, RoleSpawnFlags)
    ///   Player.AddItem(ItemType, ItemAddReason) / Pickup.Create(ItemType, Vector3)
    ///   Door.Get(string) / Door.Lock(DoorLockReason, bool) / Door.IsOpened
    ///   SpeakerToy.Create(Vector3, Quaternion, Transform, bool) / Play(float[], bool, bool)
    ///   AudioTransmitter.SampleRate = 48000
    /// </summary>
    public static class ActionExecutor
    {
        private const int AudioSampleRate = 48000;
        private const int MaxAudioSeconds = 60;

        private static readonly List<SpeakerToy> Speakers = new List<SpeakerToy>();
        private static readonly object Gate = new object();

        /// <summary>执行 timeline 中的游戏动作；未知动作仅记录日志。</summary>
        public static void Execute(string module, PlaylistItem item)
        {
            switch (item.Action.ToLowerInvariant())
            {
                case "play_audio_server":
                    PlayAudio(module, item);
                    break;
                case "change_role":
                    ChangeRole(module, item);
                    break;
                case "spawn_item":
                    SpawnItem(module, item);
                    break;
                case "control_door":
                    ControlDoor(module, item);
                    break;
                default:
                    Logger.Warn($"[Fold] {module}: 未知游戏动作 {item.Action}");
                    break;
            }
        }

        /// <summary>停止并销毁本次会话创建的所有扬声器。</summary>
        public static void StopAll()
        {
            lock (Gate)
            {
                foreach (var speaker in Speakers)
                {
                    try
                    {
                        speaker.Stop();
                        speaker.Destroy();
                    }
                    catch
                    {
                        // 回合结束时对象可能已被销毁，忽略
                    }
                }
                Speakers.Clear();
            }
        }

        // ---------------- PLAY_AUDIO ----------------
        // 服务端全局音效：SpeakerToy + 非空间音频。支持 WAV（PCM 8/16/24/32、IEEE float32），
        // 自动混为单声道并重采样到 48000Hz（AudioTransmitter.SampleRate）。
        private static void PlayAudio(string module, PlaylistItem item)
        {
            string url = MiniJson.GetStringValue(item.Raw, "url") ?? item.Url;
            if (string.IsNullOrEmpty(url))
            {
                Logger.Warn($"[Fold] {module}: PLAY_AUDIO 缺少 url");
                return;
            }
            float volume = MiniJson.GetNumberValue(item.Raw, "volume", 1f);
            bool loop = MiniJson.GetNumberValue(item.Raw, "loop", 0f) > 0f;

            _ = Task.Run(() =>
            {
                try
                {
                    var bytes = LoadBytes(url);
                    var samples = DecodeWav(bytes);
                    var speaker = SpeakerToy.Create(
                        UnityEngine.Vector3.zero,
                        UnityEngine.Quaternion.identity,
                        null,
                        true);
                    speaker.IsSpatial = false;
                    speaker.Volume = Math.Max(0f, Math.Min(1f, volume));
                    lock (Gate) { Speakers.Add(speaker); }
                    speaker.Play(samples, false, loop);
                    Logger.Info($"[Fold] {module}: PLAY_AUDIO 已播放 {samples.Length / (float)AudioSampleRate:F1}s ({url})");

                    if (!loop)
                    {
                        _ = CleanupAfter(speaker, samples.Length / (float)AudioSampleRate + 0.5f);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[Fold] {module}: PLAY_AUDIO 失败 ({url}): {ex.Message}");
                }
            });
        }

        private static async Task CleanupAfter(SpeakerToy speaker, float seconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            try
            {
                lock (Gate) { Speakers.Remove(speaker); }
                speaker.Destroy();
            }
            catch
            {
                // 忽略清理异常
            }
        }

        private static byte[] LoadBytes(string url)
        {
            if (File.Exists(url)) return File.ReadAllBytes(url);
            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "FoldEngine/1.0");
                return client.DownloadData(url);
            }
        }

        /// <summary>解析 RIFF/WAVE → 48000Hz 单声道 float 样本。</summary>
        private static float[] DecodeWav(byte[] data)
        {
            if (data.Length < 44 ||
                Encoding.ASCII.GetString(data, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(data, 8, 4) != "WAVE")
            {
                throw new InvalidDataException("仅支持 WAV (RIFF/WAVE)，其他编码请先转码");
            }

            int audioFormat = 0, channels = 0, sampleRate = 0, bits = 0;
            int dataOffset = -1, dataLength = 0;
            int pos = 12;
            while (pos + 8 <= data.Length)
            {
                string id = Encoding.ASCII.GetString(data, pos, 4);
                int size = BitConverter.ToInt32(data, pos + 4);
                int body = pos + 8;
                if (id == "fmt " && body + 16 <= data.Length)
                {
                    audioFormat = BitConverter.ToUInt16(data, body);
                    channels = BitConverter.ToUInt16(data, body + 2);
                    sampleRate = BitConverter.ToInt32(data, body + 4);
                    bits = BitConverter.ToUInt16(data, body + 14);
                }
                else if (id == "data")
                {
                    if (size <= 0) size = data.Length - body;
                    dataOffset = body;
                    dataLength = Math.Min(size, data.Length - body);
                    break;
                }
                pos = body + size + (size % 2);
            }

            if (dataOffset < 0 || channels <= 0 || sampleRate <= 0 || bits <= 0)
                throw new InvalidDataException("WAV 缺少 fmt/data 块");
            if (audioFormat != 1 && audioFormat != 3)
                throw new InvalidDataException($"不支持的 WAV 编码 (format={audioFormat})");

            int bytesPerSample = bits / 8;
            int frameCount = dataLength / (bytesPerSample * channels);
            if (frameCount <= 0) throw new InvalidDataException("WAV 没有采样数据");

            var mono = new float[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                float sum = 0f;
                for (int ch = 0; ch < channels; ch++)
                {
                    int offset = dataOffset + (i * channels + ch) * bytesPerSample;
                    sum += ReadSample(data, offset, bits, audioFormat);
                }
                mono[i] = sum / channels;
            }

            return Resample(mono, sampleRate);
        }

        private static float ReadSample(byte[] data, int offset, int bits, int audioFormat)
        {
            if (audioFormat == 3 && bits == 32) return BitConverter.ToSingle(data, offset);
            switch (bits)
            {
                case 8: return (data[offset] - 128) / 128f;
                case 16: return BitConverter.ToInt16(data, offset) / 32768f;
                case 24:
                    int v = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    return v / 8388608f;
                case 32: return BitConverter.ToInt32(data, offset) / 2147483648f;
                default: throw new InvalidDataException($"不支持的位深: {bits}");
            }
        }

        private static float[] Resample(float[] source, int sourceRate)
        {
            int maxSamples = AudioSampleRate * MaxAudioSeconds;
            if (sourceRate == AudioSampleRate)
            {
                if (source.Length <= maxSamples) return source;
                var cut = new float[maxSamples];
                Array.Copy(source, cut, maxSamples);
                return cut;
            }

            double ratio = (double)sourceRate / AudioSampleRate;
            int outLength = Math.Min((int)(source.Length / ratio), maxSamples);
            var result = new float[outLength];
            for (int i = 0; i < outLength; i++)
            {
                double src = i * ratio;
                int i0 = (int)src;
                int i1 = Math.Min(i0 + 1, source.Length - 1);
                float frac = (float)(src - i0);
                result[i] = source[i0] * (1f - frac) + source[i1] * frac;
            }
            return result;
        }

        // ---------------- CHANGE_ROLE ----------------
        private static void ChangeRole(string module, PlaylistItem item)
        {
            string playerId = MiniJson.GetStringValue(item.Raw, "playerId");
            string roleName = MiniJson.GetStringValue(item.Raw, "role");
            var player = FindPlayer(playerId);
            if (player == null)
            {
                Logger.Warn($"[Fold] {module}: CHANGE_ROLE 找不到玩家 {playerId}");
                return;
            }
            if (!Enum.TryParse(roleName, true, out RoleTypeId role))
            {
                Logger.Warn($"[Fold] {module}: CHANGE_ROLE 未知角色 {roleName}");
                return;
            }

            player.SetRole(role, RoleChangeReason.RemoteAdmin, RoleSpawnFlags.None);
            Logger.Info($"[Fold] {module}: {player.Nickname} → {role}");
        }

        // ---------------- SPAWN_ITEM ----------------
        private static void SpawnItem(string module, PlaylistItem item)
        {
            string itemName = MiniJson.GetStringValue(item.Raw, "itemId") ?? item.Url;
            if (!Enum.TryParse(itemName, true, out ItemType itemType))
            {
                Logger.Warn($"[Fold] {module}: SPAWN_ITEM 未知物品 {itemName}");
                return;
            }

            var player = FindPlayer(MiniJson.GetStringValue(item.Raw, "playerId"));
            if (player != null)
            {
                player.AddItem(itemType, ItemAddReason.AdminCommand);
                Logger.Info($"[Fold] {module}: 已向 {player.Nickname} 发放 {itemType}");
                return;
            }

            if (!TryParsePosition(MiniJson.GetStringValue(item.Raw, "position"), out var position))
            {
                position = UnityEngine.Vector3.zero;
            }
            Pickup.Create(itemType, position);
            Logger.Info($"[Fold] {module}: 已在 {position} 生成 {itemType}");
        }

        // ---------------- CONTROL_DOOR ----------------
        private static void ControlDoor(string module, PlaylistItem item)
        {
            string doorName = MiniJson.GetStringValue(item.Raw, "doorName") ?? item.Url;
            string state = (MiniJson.GetStringValue(item.Raw, "state") ?? "toggle").ToLowerInvariant();

            Door door = null;
            if (Enum.TryParse(doorName, true, out DoorName parsed))
            {
                door = Door.Get(parsed);
            }
            if (door == null && !string.IsNullOrEmpty(doorName))
            {
                door = Door.Get(doorName);
            }
            if (door == null)
            {
                Logger.Warn($"[Fold] {module}: CONTROL_DOOR 找不到门 {doorName}");
                return;
            }

            switch (state)
            {
                case "open":
                    door.IsOpened = true;
                    break;
                case "close":
                    door.IsOpened = false;
                    break;
                case "lock":
                    door.Lock(DoorLockReason.AdminCommand, true);
                    break;
                case "unlock":
                    door.Lock(DoorLockReason.AdminCommand, false);
                    break;
                case "toggle":
                    door.IsOpened = !door.IsOpened;
                    break;
                default:
                    Logger.Warn($"[Fold] {module}: CONTROL_DOOR 未知状态 {state}");
                    return;
            }
            Logger.Info($"[Fold] {module}: 门 {doorName} → {state}");
        }

        // ---------------- 工具方法 ----------------
        /// <summary>按 SteamID64（可省略 @steam）或昵称查找在线玩家。</summary>
        internal static Player FindPlayer(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return null;

            string userId = playerId.IndexOf('@') >= 0 ? playerId : playerId + "@steam";
            var player = Player.Get(userId);
            if (player != null) return player;

            foreach (var candidate in Player.List)
            {
                if (string.Equals(candidate.Nickname, playerId, StringComparison.OrdinalIgnoreCase))
                    return candidate;
                if (string.Equals(candidate.UserId, userId, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return null;
        }

        private static bool TryParsePosition(string raw, out UnityEngine.Vector3 position)
        {
            position = UnityEngine.Vector3.zero;
            if (string.IsNullOrEmpty(raw)) return false;

            var parts = raw.Split(',');
            if (parts.Length != 3) return false;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                return false;
            }
            position = new UnityEngine.Vector3(x, y, z);
            return true;
        }
    }
}
