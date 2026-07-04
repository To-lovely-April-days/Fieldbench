using System.Security.Cryptography;
using System.Text.Json;
using Fieldbench.Core.Licensing;

// Fieldbench license toolchain (vendor side — the private key never ships).
//
//   licensetool keygen                                  → new Ed25519 keypair
//   licensetool issue   --priv <b64> --email <e> [--machine <code>]... [--key <FB1-...>] [--expires <ISO>] [--out file]
//   licensetool verify  --pub <b64> --file <activation.json> [--machine <code>]
//   licensetool newkey                                  → random FB1-XXXX-XXXX-XXXX license key
//
// Typical wiring: the Paddle webhook worker calls `issue` (or reimplements it —
// the payload/signature format is 40 lines) and emails the result; the offline
// self-service page exchanges machine codes for a re-issued file bound to them.

var args0 = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (args0)
{
    case "keygen":
    {
        var (priv, pub) = LicenseManager.GenerateKeyPair();
        Console.WriteLine("PRIVATE (keep offline, never ship):");
        Console.WriteLine("  " + Convert.ToBase64String(priv));
        Console.WriteLine("PUBLIC  (embed in LicenseManager.PublicKey):");
        Console.WriteLine("  " + Convert.ToBase64String(pub));
        return 0;
    }

    case "newkey":
        Console.WriteLine(NewLicenseKey());
        return 0;

    case "issue":
    {
        string? priv = Arg("--priv");
        string? email = Arg("--email");
        if (priv is null || email is null)
        {
            Console.Error.WriteLine("issue requires --priv <base64 private key> and --email <customer email>");
            return 2;
        }

        var payload = new LicensePayload
        {
            Key = Arg("--key") ?? NewLicenseKey(),
            Email = email,
            Tier = Arg("--tier") ?? "pro",
            Machines = ArgAll("--machine"),
            IssuedUtc = DateTime.UtcNow,
            ExpiresUtc = Arg("--expires") is { } exp ? DateTime.Parse(exp).ToUniversalTime() : null,
        };

        string json = LicenseManager.Issue(payload, Convert.FromBase64String(priv));
        string outPath = Arg("--out") ?? "activation.json";
        File.WriteAllText(outPath, json);
        Console.WriteLine($"license key : {payload.Key}");
        Console.WriteLine($"machines    : {(payload.Machines.Count == 0 ? "(unbound — activates anywhere)" : string.Join(", ", payload.Machines))}");
        Console.WriteLine($"written     : {outPath}");
        return 0;
    }

    case "verify":
    {
        string? pub = Arg("--pub");
        string? file = Arg("--file");
        if (pub is null || file is null)
        {
            Console.Error.WriteLine("verify requires --pub <base64 public key> and --file <activation.json>");
            return 2;
        }

        var dir = Path.Combine(Path.GetTempPath(), "fb-lic-verify-" + Guid.NewGuid().ToString("N")[..8]);
        var manager = new LicenseManager(dir, Convert.FromBase64String(pub));
        var (ok, message) = manager.Activate(File.ReadAllText(file));
        Console.WriteLine(ok ? $"VALID — {message}" : $"INVALID — {message}");
        if (ok && Arg("--machine") is { } m)
        {
            var bound = manager.Active!.Machines;
            Console.WriteLine(bound.Count == 0 || bound.Contains(m)
                ? $"machine {m}: accepted"
                : $"machine {m}: NOT in the bound list");
        }

        return ok ? 0 : 1;
    }

    default:
        Console.WriteLine("commands: keygen | newkey | issue | verify   (see source header for options)");
        return 0;
}

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

List<string> ArgAll(string name)
{
    var result = new List<string>();
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name) result.Add(args[i + 1]);
    }

    return result;
}

static string NewLicenseKey()
{
    // FB1- prefix + 3×4 unambiguous base32 chars.
    const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    var bytes = RandomNumberGenerator.GetBytes(12);
    var chars = bytes.Select(b => alphabet[b % alphabet.Length]).ToArray();
    return $"FB1-{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
}
