using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Fieldbench.Core.Lenses;

namespace Fieldbench.App.ViewModels;

/// <summary>One cell in the byte grid, colored by field kind with a hover tooltip.</summary>
public sealed class ByteCellViewModel
{
    public required string Hex { get; init; }

    public required FieldKind Kind { get; init; }

    public required string Tooltip { get; init; }
}

public sealed class FieldRowViewModel
{
    public required FieldKind Kind { get; init; }

    public required string Name { get; init; }

    public required string Value { get; init; }

    public string? Detail { get; init; }

    public bool IsBad => Kind == FieldKind.ChecksumBad;
}

/// <summary>Byte inspector: colored byte grid + parsed field list for the selected frame.</summary>
public partial class InspectorViewModel : ObservableObject
{
    public ObservableCollection<ByteCellViewModel> Bytes { get; } = new();

    public ObservableCollection<FieldRowViewModel> Fields { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrame), nameof(IsEmpty))]
    private Frame? _frame;

    [ObservableProperty]
    private string _frameLabel = "";

    [ObservableProperty]
    private string _asciiPreview = "";

    [ObservableProperty]
    private bool _showNoLensHint;

    [ObservableProperty]
    private bool _showDetectHint;

    public bool HasFrame => Frame is not null;

    public bool IsEmpty => Frame is null;

    public void SetFrame(Frame? frame, bool rawLens, bool detectionPending)
    {
        Frame = frame;
        Bytes.Clear();
        Fields.Clear();
        ShowNoLensHint = false;
        ShowDetectHint = false;

        if (frame is null)
        {
            FrameLabel = "";
            AsciiPreview = "";
            return;
        }

        FrameLabel = $"#{frame.Id:0000} · {frame.Direction.ToString().ToUpperInvariant()} · {frame.Bytes.Length} B";
        AsciiPreview = RawLens.AsciiPreview(frame.Bytes, 48);
        ShowNoLensHint = rawLens;
        ShowDetectHint = rawLens && detectionPending;

        // Map every byte to its owning field for coloring + tooltip.
        const int maxBytes = 64;
        for (int i = 0; i < frame.Bytes.Length && i < maxBytes; i++)
        {
            var field = FindField(frame, i);
            var kind = field?.Kind ?? FieldKind.Data;
            if (rawLens) kind = FieldKind.Data;
            byte b = frame.Bytes[i];
            string tooltip = field is { } f
                ? $"{f.Name} · 0x{b:X2} · {b} · 0b{Convert.ToString(b, 2).PadLeft(8, '0')}"
                : $"0x{b:X2} · {b} · 0b{Convert.ToString(b, 2).PadLeft(8, '0')}";
            Bytes.Add(new ByteCellViewModel { Hex = b.ToString("X2"), Kind = kind, Tooltip = tooltip });
        }

        if (!rawLens)
        {
            foreach (var f in frame.Fields)
            {
                Fields.Add(new FieldRowViewModel { Kind = f.Kind, Name = f.Name, Value = f.Value, Detail = f.Detail });
            }
        }
    }

    private static FrameField? FindField(Frame frame, int offset)
    {
        foreach (var f in frame.Fields)
        {
            if (offset >= f.Offset && offset < f.Offset + f.Count) return f;
        }

        return null;
    }
}
