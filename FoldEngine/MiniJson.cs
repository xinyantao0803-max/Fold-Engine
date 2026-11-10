using System;
using System.Globalization;
using System.Text;

namespace FoldEngine
{
    /// <summary>
    /// 迷你 JSON 解析工具：纯字符串操作，零第三方依赖。
    /// 仅用于解析 FoldEdge 主程序下发的受控格式数据。
    /// </summary>
    public static class MiniJson
    {
        /// <summary>定位 "key" 后的冒号位置（跳过字符串值中的同名片段，键名忽略大小写）。</summary>
        public static bool TryFindKey(string json, string key, out int colonPos)
        {
            var marker = "\"" + key + "\"";
            int idx = 0;
            while (true)
            {
                idx = json.IndexOf(marker, idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    colonPos = -1;
                    return false;
                }
                int c = idx + marker.Length;
                while (c < json.Length && (json[c] == ' ' || json[c] == '\t')) c++;
                if (c < json.Length && json[c] == ':')
                {
                    colonPos = c;
                    return true;
                }
                idx = c;
            }
        }

        /// <summary>提取 "key" 对应的字符串值，支持 \" \\ \n \r \t 转义。</summary>
        public static string GetStringValue(string json, string key)
        {
            if (!TryFindKey(json, key, out var colon)) return null;

            int start = -1;
            for (int i = colon + 1; i < json.Length; i++)
            {
                if (json[i] == '"')
                {
                    start = i;
                    break;
                }
                if (json[i] != ' ' && json[i] != '\t') return null;
            }
            if (start < 0) return null;

            var sb = new StringBuilder();
            for (int i = start + 1; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    switch (n)
                    {
                        case '"': sb.Append('"'); i++; continue;
                        case '\\': sb.Append('\\'); i++; continue;
                        case 'n': sb.Append('\n'); i++; continue;
                        case 'r': sb.Append('\r'); i++; continue;
                        case 't': sb.Append('\t'); i++; continue;
                        default: sb.Append(n); i++; continue;
                    }
                }
                if (ch == '"') break;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>提取 "key" 对应的数值（float）。</summary>
        public static float GetNumberValue(string json, string key, float fallback)
        {
            if (!TryFindKey(json, key, out var colon)) return fallback;

            int i = colon + 1;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            int start = i;
            while (i < json.Length &&
                   (char.IsDigit(json[i]) || json[i] == '.' || json[i] == '-' ||
                    json[i] == '+' || json[i] == 'e' || json[i] == 'E'))
            {
                i++;
            }
            if (i == start) return fallback;
            return float.TryParse(json.Substring(start, i - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        /// <summary>提取 "key" 对应的数组内容（不含两端的方括号），无则返回 null。</summary>
        public static string ExtractArrayBody(string json, string key)
        {
            if (!TryFindKey(json, key, out var colon)) return null;
            var open = json.IndexOf('[', colon + 1);
            if (open < 0) return null;
            var close = json.LastIndexOf(']');
            if (close <= open) return null;
            var body = json.Substring(open + 1, close - open - 1).Trim();
            return body.Length == 0 ? null : body;
        }

        /// <summary>把数组内容按 "},{" 切分为已去除首尾花括号的对象片段。</summary>
        public static string[] SplitObjects(string body)
        {
            var raw = body.Split(new[] { "},{" }, StringSplitOptions.RemoveEmptyEntries);
            var result = new string[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                var chunk = raw[i].Trim();
                if (chunk.StartsWith("{")) chunk = chunk.Substring(1);
                if (chunk.EndsWith("}")) chunk = chunk.Substring(0, chunk.Length - 1);
                result[i] = chunk;
            }
            return result;
        }
    }
}
