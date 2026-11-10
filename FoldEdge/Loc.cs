using System.Text.Json;

namespace FoldEdge;

/// <summary>
/// 多语言资源（预留能力）：从 locales/{lang}.json 读取词条，
/// 支持 {0}/{1}... 占位符。当前默认 zh-CN，后续可从配置或请求头切换。
/// </summary>
public static class Loc
{
    private static readonly Dictionary<string, string> Entries = new();
    private static string _current = "zh-CN";

    /// <summary>当前语言。</summary>
    public static string Current => _current;

    /// <summary>加载指定语言资源；文件缺失时保持现状。</summary>
    public static void Load(string lang)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "locales", lang + ".json");
        if (!File.Exists(path)) return;

        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        if (dict == null) return;

        Entries.Clear();
        foreach (var kv in dict)
        {
            Entries[kv.Key] = kv.Value;
        }
        _current = lang;
    }

    /// <summary>取词条；支持占位符。未命中时返回 key 本身。</summary>
    public static string Get(string key, params object?[] args)
    {
        if (!Entries.TryGetValue(key, out var template)) return key;
        return args.Length == 0 ? template : string.Format(template, args);
    }
}
