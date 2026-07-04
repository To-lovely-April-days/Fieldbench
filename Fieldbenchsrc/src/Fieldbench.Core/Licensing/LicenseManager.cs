using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Fieldbench.Core.Licensing;

public enum LicenseTier
{
    Free,
    TrialPro,
    Pro,
}

/// <summary>Signed license payload (the JSON inside an activation file).</summary>
public sealed class LicensePayload
{
    public string Key { get; set; } = "";

    public string Email { get; set; } = "";

    public string Tier { get; set; } = "pro";

    /// <summary>Machine codes this license is bound to (max 2 per PRD).</summary>
    public List<string> Machines { get; set; } = new();

    public DateTime IssuedUtc { get; set; }

    /// <summary>Perpetual licenses have no expiry — activation never phones home.</summary>
    public DateTime? ExpiresUtc { get; set; }
}

public sealed class LicenseFile
{
    public string Payload { get; set; } = "";

    public string Signature { get; set; } = "";
}

/// <summary>
/// Ed25519-verified licensing with offline activation. The machine code is a
/// stable hardware-derived fingerprint; the activation file (from the website
/// self-service page or email) embeds it, so activation works with zero
/// connectivity — and stays valid forever, no phone-home (the PRD red line).
/// </summary>
public sealed class LicenseManager
{
    // Product public key (Ed25519, 32 bytes). The private half lives offline with releases;
    // this development pair is regenerated before any real shipment.
    private static readonly byte[] PublicKey = Convert.FromBase64String("u5kIlvaRKZarPEkFWurymbY91dDCeN0UhToTdN1R07k=");

    private readonly string _statePath;

    public LicenseManager(string stateDirectory, byte[]? publicKeyOverride = null)
    {
        Directory.CreateDirectory(stateDirectory);
        _statePath = Path.Combine(stateDirectory, "license.json");
        EffectivePublicKey = publicKeyOverride ?? PublicKey;
        Load();
    }

    public byte[] EffectivePublicKey { get; }

    public LicensePayload? Active { get; private set; }

    public DateTime? TrialStartedUtc { get; set; }

    public event Action? Changed;

    public LicenseTier Tier
    {
        get
        {
            if (Active is { } lic && (lic.ExpiresUtc is null || lic.ExpiresUtc > DateTime.UtcNow)) return LicenseTier.Pro;
            if (TrialStartedUtc is { } start && DateTime.UtcNow - start < TimeSpan.FromDays(14)) return LicenseTier.TrialPro;
            return LicenseTier.Free;
        }
    }

    public bool IsProUnlocked => Tier is LicenseTier.Pro or LicenseTier.TrialPro;

    public int TrialDaysLeft => TrialStartedUtc is { } start
        ? Math.Max(0, 14 - (int)(DateTime.UtcNow - start).TotalDays)
        : 14;

    /// <summary>Free tier limits (PRD §8.1).</summary>
    public int MaxConnections => IsProUnlocked ? int.MaxValue : 1;

    public int MaxChartChannels => IsProUnlocked ? int.MaxValue : 2;

    public bool SlaveSimulationAllowed => IsProUnlocked;

    /// <summary>Stable machine fingerprint, grouped XXXX-XXXX-XXXX-XXXX.</summary>
    public static string MachineCode()
    {
        string seed = MachineSeed();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("fieldbench:" + seed));
        var hex = Convert.ToHexString(hash)[..16];
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    private static string MachineSeed()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (key?.GetValue("MachineGuid") is string guid) return guid;
            }
            else if (File.Exists("/etc/machine-id"))
            {
                return File.ReadAllText("/etc/machine-id").Trim();
            }
        }
        catch
        {
            // Fall through to the environment-derived seed.
        }

        return $"{Environment.MachineName}|{Environment.OSVersion.Platform}|{Environment.ProcessorCount}";
    }

    /// <summary>Verify + activate an activation file (offline path: imported from disk).</summary>
    public (bool Ok, string Message) Activate(string licenseFileJson)
    {
        LicenseFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LicenseFile>(licenseFileJson, JsonOpts);
        }
        catch (JsonException)
        {
            return (false, "Not a valid activation file.");
        }

        if (file is null || string.IsNullOrEmpty(file.Payload) || string.IsNullOrEmpty(file.Signature))
        {
            return (false, "Not a valid activation file.");
        }

        byte[] payloadBytes, signature;
        try
        {
            payloadBytes = Convert.FromBase64String(file.Payload);
            signature = Convert.FromBase64String(file.Signature);
        }
        catch (FormatException)
        {
            return (false, "Activation file is corrupted.");
        }

        if (!VerifySignature(payloadBytes, signature))
        {
            return (false, "Signature check failed — this file was not issued for this product.");
        }

        LicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes, JsonOpts);
        }
        catch (JsonException)
        {
            return (false, "Activation file payload is malformed.");
        }

        if (payload is null) return (false, "Activation file payload is malformed.");

        var machine = MachineCode();
        if (payload.Machines.Count > 0 && !payload.Machines.Contains(machine))
        {
            return (false, $"This license is bound to other machines. This machine's code is {machine} — rebind it on the website.");
        }

        if (payload.ExpiresUtc is { } exp && exp < DateTime.UtcNow)
        {
            return (false, "This license has expired.");
        }

        Active = payload;
        Save();
        Changed?.Invoke();
        return (true, $"Activated — licensed to {payload.Email}.");
    }

    public void Deactivate()
    {
        Active = null;
        Save();
        Changed?.Invoke();
    }

    public void StartTrial()
    {
        TrialStartedUtc ??= DateTime.UtcNow;
        Save();
        Changed?.Invoke();
    }

    private bool VerifySignature(byte[] payload, byte[] signature)
    {
        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(EffectivePublicKey, 0));
            verifier.BlockUpdate(payload, 0, payload.Length);
            return verifier.VerifySignature(signature);
        }
        catch
        {
            return false;
        }
    }

    // ── persistence ──

    private sealed class State
    {
        public LicensePayload? Active { get; set; }

        public DateTime? TrialStartedUtc { get; set; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath), JsonOpts);
            Active = state?.Active;
            TrialStartedUtc = state?.TrialStartedUtc;
        }
        catch
        {
            // Corrupt state falls back to Free; the license file can be re-imported.
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_statePath, JsonSerializer.Serialize(new State
            {
                Active = Active,
                TrialStartedUtc = TrialStartedUtc,
            }, JsonOpts));
        }
        catch
        {
            // Read-only install dir (portable zip on locked-down machines) — licensing stays in-memory.
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Issue a signed license (release tooling + tests; the private key never ships).</summary>
    public static string Issue(LicensePayload payload, byte[] privateKey)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts);
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey, 0));
        signer.BlockUpdate(payloadBytes, 0, payloadBytes.Length);
        var signature = signer.GenerateSignature();
        return JsonSerializer.Serialize(new LicenseFile
        {
            Payload = Convert.ToBase64String(payloadBytes),
            Signature = Convert.ToBase64String(signature),
        }, JsonOpts);
    }

    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var random = new Org.BouncyCastle.Security.SecureRandom();
        var priv = new Ed25519PrivateKeyParameters(random);
        return (priv.GetEncoded(), priv.GeneratePublicKey().GetEncoded());
    }
}
