using System.Security.Cryptography;
using System.Text;

namespace FoldEdge;

/// <summary>
/// VPN credential lifecycle + Windows native VPN profile generator.
///
/// FoldEdge manages the account lifecycle (create / verify / disable) and emits
/// a PowerShell script that creates a native Windows VPN profile via
/// Add-VpnConnection. The actual tunnel is terminated by the L2TP/IKEv2 gateway
/// configured with VpnServer; a gateway can call /api/vpn/verify before
/// accepting a connection, so revoking a user takes effect immediately.
/// </summary>
public sealed class VpnService
{
    private readonly string _server;
    private readonly string _tunnelType;

    public VpnService(string server, string tunnelType)
    {
        _server = string.IsNullOrWhiteSpace(server) ? "vpn.example.com" : server.Trim();
        _tunnelType = string.IsNullOrWhiteSpace(tunnelType) ? "L2tp" : tunnelType.Trim();
    }

    public string Server => _server;
    public string TunnelType => _tunnelType;

    public VpnAccount CreateAccount()
    {
        return new VpnAccount
        {
            Username = "fold_" + RandomHex(8),
            Password = RandomPassword(20),
            Enabled = true,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public string BuildWindowsScript(FoldUser user)
    {
        var vpn = user.Vpn ?? throw new InvalidOperationException("user has no VPN account");
        bool ikev2 = _tunnelType.Equals("IKEv2", StringComparison.OrdinalIgnoreCase);
        string tunnel = ikev2 ? "IKEv2" : "L2tp";
        string auth = ikev2 ? "EAP" : "MSChapv2";

        return $"""
# ============================================================================
# Fold VPN profile - Windows native VPN (L2TP/IPsec or IKEv2)
# Generated for {user.Username} ({user.Role})
#
# Run in an elevated PowerShell window:
#   powershell -ExecutionPolicy Bypass -File .\Fold-{user.Username}.ps1
#
# The gateway is configured by the Fold server owner. FoldEdge revokes this
# credential automatically when the account expires or is disabled.
# ============================================================================
$name   = "Fold-{user.Username}"
$server = "{_server}"
$vpnUser = "{vpn.Username}"
$vpnPass = "{vpn.Password}"

Remove-VpnConnection -Name $name -Force -ErrorAction SilentlyContinue

Add-VpnConnection `
  -Name $name `
  -ServerAddress $server `
  -TunnelType {tunnel} `
  -EncryptionLevel Required `
  -AuthenticationMethod {auth} `
  -RememberCredential `
  -SplitTunneling `
  -Force

Write-Host ""
Write-Host "Fold VPN profile created: $name" -ForegroundColor Green
Write-Host "Server : $server"
Write-Host "User   : $vpnUser"
Write-Host "Pass   : $vpnPass"
Write-Host ""
Write-Host "Connect via Settings > Network & internet > VPN, or run:" -ForegroundColor Cyan
Write-Host "  rasdial `"$name`" `"$vpnUser`" `"$vpnPass`""
""";
    }

    private static string RandomHex(int byteCount)
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();
    }

    private static string RandomPassword(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
        byte[] bytes = RandomNumberGenerator.GetBytes(length);
        var builder = new StringBuilder(length);
        foreach (byte value in bytes)
            builder.Append(alphabet[value % alphabet.Length]);
        return builder.ToString();
    }
}
