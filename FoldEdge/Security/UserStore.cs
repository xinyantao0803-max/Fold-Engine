using System.Text.Json;

namespace FoldEdge;

/// <summary>Thread-safe JSON-backed user store at Data/users.json.</summary>
public sealed class UserStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<FoldUser> _users;

    public UserStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "users.json");
        _users = Load();
    }

    public bool HasOwner
    {
        get
        {
            lock (_gate)
                return _users.Any(user => user.Role == "Owner");
        }
    }

    public IReadOnlyList<FoldUser> All()
    {
        lock (_gate)
            return _users.Select(Clone).ToList();
    }

    public FoldUser? FindById(string id)
    {
        lock (_gate)
            return CloneOrNull(_users.FirstOrDefault(user => user.Id == id));
    }

    public FoldUser? FindByUsername(string username)
    {
        lock (_gate)
            return CloneOrNull(_users.FirstOrDefault(user =>
                string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase)));
    }

    public FoldUser? FindByVpnUsername(string username)
    {
        lock (_gate)
            return CloneOrNull(_users.FirstOrDefault(user =>
                user.Vpn != null &&
                string.Equals(user.Vpn.Username, username, StringComparison.OrdinalIgnoreCase)));
    }

    public void Add(FoldUser user)
    {
        lock (_gate)
        {
            _users.Add(user);
            SaveLocked();
        }
    }

    public bool Update(string id, Action<FoldUser> mutate)
    {
        lock (_gate)
        {
            var user = _users.FirstOrDefault(item => item.Id == id);
            if (user == null)
                return false;

            mutate(user);
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Atomically revokes a user: bumps TokenVersion, disables VPN, records the time.
    /// Returns false when the user is missing or already revoked.
    /// </summary>
    public bool TryMarkRevoked(string id, out FoldUser? revoked)
    {
        lock (_gate)
        {
            var user = _users.FirstOrDefault(item => item.Id == id);
            if (user == null || user.RevokedAtUtc != null)
            {
                revoked = null;
                return false;
            }

            user.RevokedAtUtc = DateTime.UtcNow;
            user.TokenVersion++;
            if (user.Vpn != null)
                user.Vpn.Enabled = false;

            SaveLocked();
            revoked = Clone(user);
            return true;
        }
    }

    private List<FoldUser> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new List<FoldUser>();

            var json = File.ReadAllText(_path);
            var users = JsonSerializer.Deserialize<List<FoldUser>>(json, JsonOptions);
            return users ?? new List<FoldUser>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FoldEdge] users.json load failed: {ex.Message}");
            return new List<FoldUser>();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_users, JsonOptions));
            File.Move(temp, _path, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FoldEdge] users.json save failed: {ex.Message}");
        }
    }

    private static FoldUser? CloneOrNull(FoldUser? user) => user == null ? null : Clone(user);

    private static FoldUser Clone(FoldUser user)
    {
        return new FoldUser
        {
            Id = user.Id,
            Username = user.Username,
            PasswordHash = user.PasswordHash,
            PasswordSalt = user.PasswordSalt,
            Role = user.Role,
            SteamId = user.SteamId,
            CreatedAtUtc = user.CreatedAtUtc,
            ExpiresAtUtc = user.ExpiresAtUtc,
            RevokedAtUtc = user.RevokedAtUtc,
            TokenVersion = user.TokenVersion,
            Vpn = user.Vpn == null
                ? null
                : new VpnAccount
                {
                    Username = user.Vpn.Username,
                    Password = user.Vpn.Password,
                    Enabled = user.Vpn.Enabled,
                    CreatedAtUtc = user.Vpn.CreatedAtUtc
                }
        };
    }
}
