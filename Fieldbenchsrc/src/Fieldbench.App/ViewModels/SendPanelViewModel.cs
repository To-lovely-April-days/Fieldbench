using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Profiles;

namespace Fieldbench.App.ViewModels;

/// <summary>
/// Raw terminal / sender: hex or ASCII with escapes, checksum append,
/// cyclic send, history and named favorites (PRD §6.3).
/// </summary>
public partial class SendPanelViewModel : ObservableObject
{
    private readonly SessionViewModel _session;
    private CancellationTokenSource? _cycleCts;

    public SendPanelViewModel(SessionViewModel session)
    {
        _session = session;
        foreach (var fav in session.Main.Workbench.Settings.Favorites) Favorites.Add(fav);
        foreach (var h in session.Main.Workbench.Settings.SendHistory.Take(6)) History.Add(h);
    }

    [ObservableProperty]
    private bool _isHexMode = true;

    [ObservableProperty]
    private string _payload = "";

    [ObservableProperty]
    private ChecksumKind _checksum = ChecksumKind.Crc16Modbus;

    public static ChecksumKind[] ChecksumKinds { get; } =
        [ChecksumKind.None, ChecksumKind.Crc16Modbus, ChecksumKind.Crc16Ccitt, ChecksumKind.Xor, ChecksumKind.Sum];

    [ObservableProperty]
    private bool _cycleEnabled;

    [ObservableProperty]
    private int _cycleIntervalMs = 1000;

    [ObservableProperty]
    private int _cycleCount; // 0 = infinite

    [ObservableProperty]
    private bool _isCycling;

    [ObservableProperty]
    private string? _error;

    public ObservableCollection<FavoriteFrame> Favorites { get; } = new();

    public ObservableCollection<string> History { get; } = new();

    /// <summary>Parse the editor content: hex pairs or ASCII with \r \n \xNN \\ escapes.</summary>
    public byte[]? TryParsePayload(out string? error)
    {
        error = null;
        var text = Payload.Trim();
        if (text.Length == 0)
        {
            error = "Nothing to send.";
            return null;
        }

        if (IsHexMode)
        {
            var clean = Regex.Replace(text, @"[\s,;:\-]+", "");
            if (clean.Length % 2 != 0 || !Regex.IsMatch(clean, @"^[0-9A-Fa-f]*$"))
            {
                error = "Hex must be pairs of 0-9 A-F.";
                return null;
            }

            return Convert.FromHexString(clean);
        }

        var bytes = new List<byte>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                switch (next)
                {
                    case 'r': bytes.Add(0x0D); i++; continue;
                    case 'n': bytes.Add(0x0A); i++; continue;
                    case 't': bytes.Add(0x09); i++; continue;
                    case '0': bytes.Add(0x00); i++; continue;
                    case '\\': bytes.Add((byte)'\\'); i++; continue;
                    case 'x' when i + 3 < text.Length
                                  && byte.TryParse(text.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx):
                        bytes.Add(hx);
                        i += 3;
                        continue;
                }
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
        }

        return bytes.ToArray();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var payload = TryParsePayload(out var error);
        if (payload is null)
        {
            Error = error;
            return;
        }

        Error = null;
        var data = ChecksumAppender.Append(payload, Checksum);

        if (CycleEnabled && !IsCycling)
        {
            _cycleCts = new CancellationTokenSource();
            IsCycling = true;
            _ = CycleLoopAsync(data, _cycleCts.Token);
        }
        else if (IsCycling)
        {
            StopCycle();
        }
        else
        {
            await SendOnceAsync(data);
        }
    }

    private async Task CycleLoopAsync(byte[] data, CancellationToken ct)
    {
        int sent = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await SendOnceAsync(data);
                sent++;
                if (CycleCount > 0 && sent >= CycleCount) break;
                await Task.Delay(Math.Max(10, CycleIntervalMs), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsCycling = false);
        }
    }

    public void StopCycle()
    {
        _cycleCts?.Cancel();
        IsCycling = false;
    }

    private async Task SendOnceAsync(byte[] data)
    {
        try
        {
            await _session.Session.Connection.SendAsync(data);
            var hex = Convert.ToHexString(data);
            var display = string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                History.Remove(display);
                History.Insert(0, display);
                while (History.Count > 6) History.RemoveAt(History.Count - 1);
                var settings = _session.Main.Workbench.Settings;
                settings.SendHistory.Remove(display);
                settings.SendHistory.Insert(0, display);
                while (settings.SendHistory.Count > 30) settings.SendHistory.RemoveAt(settings.SendHistory.Count - 1);
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Error = ex.Message);
        }
    }

    [RelayCommand]
    private void SaveFavorite()
    {
        var payload = TryParsePayload(out _);
        if (payload is null) return;
        var fav = new FavoriteFrame
        {
            Name = $"Frame {Favorites.Count + 1}",
            HexPayload = Convert.ToHexString(payload),
            Checksum = Checksum,
        };
        Favorites.Add(fav);
        _session.Main.Workbench.Settings.Favorites.Add(fav);
        _session.Main.Workbench.SettingsStore.Save();
    }

    [RelayCommand]
    private void LoadFavorite(FavoriteFrame fav)
    {
        IsHexMode = true;
        var hex = fav.HexPayload;
        Payload = string.Join(" ", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
        Checksum = fav.Checksum;
    }

    [RelayCommand]
    private void LoadHistory(string entry)
    {
        IsHexMode = true;
        Payload = entry;
        Checksum = ChecksumKind.None;
    }
}
