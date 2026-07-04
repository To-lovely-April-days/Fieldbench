using System.Text.Json;
using System.Text.Json.Serialization;
using Fieldbench.Core.Ai;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Transport;

namespace Fieldbench.Core.Profiles;

/// <summary>A named, saved connection ("Boiler skid A") for one-click reconnect.</summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";

    public ConnectionConfig Config { get; set; } = new();

    /// <summary>Session kind to open with (0 Monitor · 1 Master · 2 Slave).</summary>
    public int StartAs { get; set; } = 1;

    public DateTime LastUsedUtc { get; set; }
}

/// <summary>A favorite frame in the raw sender.</summary>
public sealed class FavoriteFrame
{
    public string Name { get; set; } = "";

    public string HexPayload { get; set; } = "";

    public ChecksumKind Checksum { get; set; } = ChecksumKind.None;
}

/// <summary>Serialized device point map (share/reuse across benches).</summary>
public sealed class DeviceProfile
{
    public string Device { get; set; } = "";

    public string Source { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    public List<DeviceProfilePoint> Points { get; set; } = new();
}

public sealed class DeviceProfilePoint
{
    public string Name { get; set; } = "";

    public RegisterArea Area { get; set; }

    public int Address { get; set; }

    public RegisterDataType Type { get; set; }

    public WordOrder Order { get; set; }

    public double Scale { get; set; } = 1;

    public double Offset { get; set; }

    public string Unit { get; set; } = "";

    public bool Writable { get; set; }

    public string Notes { get; set; } = "";
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Light";

    public string Language { get; set; } = "en";

    public int TimelineBufferFrames { get; set; } = 100_000;

    public double RawSplitGapMs { get; set; } = 20;

    public string AiGatewayUrl { get; set; } = "https://ai.fieldbench.app/";

    /// <summary>Online activation endpoint (POST {key, machine} → activation file).</summary>
    public string ActivationApiUrl { get; set; } = "https://fieldbench.app/api/activate";

    public AiQuota AiQuota { get; set; } = new();

    /// <summary>Month key ("2026-07") the quota counters apply to.</summary>
    public string AiQuotaMonth { get; set; } = "";

    public bool AiPrivacyAccepted { get; set; }

    public DateTime? FirstRunUtc { get; set; }

    public bool TelemetryOptIn { get; set; }

    public bool CheckUpdates { get; set; } = true;

    public List<ConnectionProfile> Profiles { get; set; } = new();

    public List<FavoriteFrame> Favorites { get; set; } = new();

    public List<string> SendHistory { get; set; } = new();
}

/// <summary>JSON settings persistence in the per-user app data directory.</summary>
public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string? directory = null)
    {
        Directory_ = directory ?? DefaultDirectory();
        Directory.CreateDirectory(Directory_);
        _path = Path.Combine(Directory_, "settings.json");
        Settings = Load();
        RollQuotaMonth();
    }

    public string Directory_ { get; }

    public AppSettings Settings { get; }

    public static string DefaultDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(string.IsNullOrEmpty(baseDir) ? "." : baseDir, "Fieldbench");
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOpts) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt settings reset to defaults rather than blocking startup.
        }

        return new AppSettings();
    }

    /// <summary>
    /// Monthly AI quota reset — subscribers only (local side of the dual counting).
    /// The free allowance (30 explains + 3 extractions) is a one-time gift and
    /// never refills (PRD §6.7/§6.8).
    /// </summary>
    private void RollQuotaMonth()
    {
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        if (Settings.AiQuotaMonth != month)
        {
            Settings.AiQuotaMonth = month;
            if (Settings.AiQuota.Subscribed)
            {
                Settings.AiQuota.ExplainsLimit = 1000;
                Settings.AiQuota.ExtractionsLimit = 30;
                Settings.AiQuota.ExplainsUsed = 0;
                Settings.AiQuota.ExtractionsUsed = 0;
            }

            Save();
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Settings, JsonOpts));
        }
        catch
        {
            // Locked-down machines (portable zip) may be read-only; run with in-memory settings.
        }
    }

    // ── device profiles ──

    public string DeviceProfilesDirectory => Path.Combine(Directory_, "devices");

    public void SaveDeviceProfile(DeviceProfile profile, string fileName)
    {
        Directory.CreateDirectory(DeviceProfilesDirectory);
        File.WriteAllText(Path.Combine(DeviceProfilesDirectory, fileName), JsonSerializer.Serialize(profile, JsonOpts));
    }

    public DeviceProfile? LoadDeviceProfile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
