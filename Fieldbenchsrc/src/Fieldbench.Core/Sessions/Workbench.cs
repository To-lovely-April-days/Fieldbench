using Fieldbench.Core.Ai;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Licensing;
using Fieldbench.Core.Profiles;
using Fieldbench.Core.Transport;

namespace Fieldbench.Core.Sessions;

/// <summary>
/// Root application model: connections, sessions, licensing, settings, AI.
/// Free tier: 1 active connection; slave simulation is Pro (demo excluded).
/// </summary>
public sealed class Workbench : IAsyncDisposable
{
    public Workbench(SettingsStore? settingsStore = null)
    {
        SettingsStore = settingsStore ?? new SettingsStore();
        License = new LicenseManager(SettingsStore.Directory_);
        AiClient = new GatewayAiClient(SettingsStore.Settings.AiGatewayUrl, License.Active?.Key);
        Settings.FirstRunUtc ??= DateTime.UtcNow;
    }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings => SettingsStore.Settings;

    public LicenseManager License { get; }

    public IAiClient AiClient { get; set; }

    public List<Connection> Connections { get; } = new();

    public event Action? ConnectionsChanged;

    public bool IsFirstRun => Connections.Count == 0 && Settings.Profiles.Count == 0;

    /// <summary>Demo connections don't count toward the Free limit (PRD §6.11).</summary>
    public bool CanAddConnection =>
        Connections.Count(c => c.Config.Kind != ConnectionKind.Loopback) < License.MaxConnections;

    public Connection AddConnection(ConnectionConfig config, string? name = null)
    {
        if (config.Kind != ConnectionKind.Loopback && !CanAddConnection)
        {
            throw new ProFeatureException("Free includes 1 active connection. Loopback self-tests need 2 — that's Pro.");
        }

        var connection = new Connection(config, name);
        Connections.Add(connection);
        ConnectionsChanged?.Invoke();
        return connection;
    }

    public Connection AddConnection(Connection connection)
    {
        Connections.Add(connection);
        ConnectionsChanged?.Invoke();
        return connection;
    }

    public async Task RemoveConnectionAsync(Connection connection)
    {
        Connections.Remove(connection);
        await connection.DisposeAsync().ConfigureAwait(false);
        ConnectionsChanged?.Invoke();
    }

    public Session CreateSession(Connection connection, SessionKind kind, IProtocolLens lens, string? name = null)
    {
        if (kind == SessionKind.Slave && !License.SlaveSimulationAllowed && connection.Config.Kind != ConnectionKind.Loopback)
        {
            throw new ProFeatureException("Slave simulation is a Pro feature — start the 14-day trial or upgrade.");
        }

        var session = new Session(connection, kind, lens, name)
        {
            Detector = kind == SessionKind.Monitor ? new ProtocolDetector() : null,
        };
        session.MaxFrames = Settings.TimelineBufferFrames;
        ConnectionsChanged?.Invoke();
        return session;
    }

    /// <summary>Consume one AI explain credit; false when the quota is exhausted.</summary>
    public bool TryConsumeExplain()
    {
        var q = Settings.AiQuota;
        if (q.ExplainsLeft <= 0) return false;
        q.ExplainsUsed++;
        SettingsStore.Save();
        return true;
    }

    public bool TryConsumeExtraction()
    {
        var q = Settings.AiQuota;
        if (q.ExtractionsLeft <= 0) return false;
        q.ExtractionsUsed++;
        SettingsStore.Save();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in Connections.ToArray())
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        Connections.Clear();
        SettingsStore.Save();
    }
}

public sealed class ProFeatureException(string message) : Exception(message);
