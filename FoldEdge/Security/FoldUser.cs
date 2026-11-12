namespace FoldEdge;

/// <summary>Dashboard account. Password material never leaves the server.</summary>
public sealed class FoldUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;

    /// <summary>Owner | Admin | Viewer.</summary>
    public string Role { get; set; } = "Viewer";

    /// <summary>Optional in-game SteamID64; used for the expiry kill switch.</summary>
    public string SteamId { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Bumped on revoke/expiry so every issued JWT becomes invalid immediately.</summary>
    public int TokenVersion { get; set; } = 1;

    public VpnAccount? Vpn { get; set; }
}

/// <summary>VPN credential bound to a dashboard account (L2TP / IKEv2 gateway).</summary>
public sealed class VpnAccount
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
