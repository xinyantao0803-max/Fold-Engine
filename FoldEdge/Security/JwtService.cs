using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FoldEdge;

public sealed class JwtPayload
{
    public string Sub { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public int Ver { get; set; }
    public long Exp { get; set; }
    public long Iat { get; set; }
}

/// <summary>
/// Minimal HS256 JWT implementation. The signing secret is generated once and
/// persisted to Data/jwt-secret.b64, so restarting FoldEdge keeps sessions valid
/// until they expire or a user's TokenVersion is bumped.
/// </summary>
public sealed class JwtService
{
    private readonly byte[] _secret;

    public JwtService(string secretPath)
    {
        if (File.Exists(secretPath))
        {
            _secret = Convert.FromBase64String(File.ReadAllText(secretPath).Trim());
        }
        else
        {
            _secret = RandomNumberGenerator.GetBytes(64);
            File.WriteAllText(secretPath, Convert.ToBase64String(_secret));
        }
    }

    public string Issue(FoldUser user, TimeSpan lifetime)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long exp = now + (long)lifetime.TotalSeconds;
        if (user.ExpiresAtUtc.HasValue)
        {
            long userExp = new DateTimeOffset(user.ExpiresAtUtc.Value).ToUnixTimeSeconds();
            exp = Math.Min(exp, userExp);
        }

        var payload = new JwtPayload
        {
            Sub = user.Id,
            Name = user.Username,
            Role = user.Role,
            Ver = user.TokenVersion,
            Exp = exp,
            Iat = now
        };

        string header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        string body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        string signature = Base64Url(Sign(header + "." + body));
        return header + "." + body + "." + signature;
    }

    public bool TryValidate(string token, out JwtPayload? payload, out string error)
    {
        payload = null;
        error = "invalid";

        string[] parts = token.Split('.');
        if (parts.Length != 3)
            return false;

        string data = parts[0] + "." + parts[1];
        byte[] expected = Sign(data);
        byte[]? actual = Base64UrlDecode(parts[2]);
        if (actual == null || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            error = "bad-signature";
            return false;
        }

        try
        {
            byte[]? body = Base64UrlDecode(parts[1]);
            if (body == null)
                return false;

            var parsed = JsonSerializer.Deserialize<JwtPayload>(body);
            if (parsed == null)
                return false;

            if (parsed.Exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                error = "expired";
                return false;
            }

            payload = parsed;
            error = "ok";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private byte[] Sign(string data)
    {
        using var hmac = new HMACSHA256(_secret);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Base64Url(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[]? Base64UrlDecode(string value)
    {
        try
        {
            string text = value.Replace('-', '+').Replace('_', '/');
            switch (text.Length % 4)
            {
                case 2: text += "=="; break;
                case 3: text += "="; break;
            }

            return Convert.FromBase64String(text);
        }
        catch
        {
            return null;
        }
    }
}
