using Fieldbench.Core.Master;
using Fieldbench.Core.Modbus;

namespace Fieldbench.Core.Slave;

public enum GeneratorKind
{
    Static,
    Increment,
    RandomRange,
    Sine,
}

/// <summary>
/// A value generator bound to a slave tag: static / increment / random range /
/// sine (amplitude + period). The core selling point for no-device development
/// and the demo (PRD §6.5).
/// </summary>
public sealed class ValueGenerator
{
    public GeneratorKind Kind { get; set; } = GeneratorKind.Static;

    // Increment
    public double Step { get; set; } = 1;

    /// <summary>Step is applied per second, fractionally per tick.</summary>
    public double IncrementPerSecond { get; set; } = 1;

    public double WrapMin { get; set; }

    public double WrapMax { get; set; } = 1000;

    // Random
    public double Min { get; set; }

    public double Max { get; set; } = 100;

    // Sine
    public double SineMin { get; set; } = 20;

    public double SineMax { get; set; } = 30;

    public double PeriodSeconds { get; set; } = 10;

    private double _phaseOffset = Random.Shared.NextDouble() * Math.PI * 2;
    private double _current;
    private bool _initialized;

    public string Label() => Kind switch
    {
        GeneratorKind.Static => "Static",
        GeneratorKind.Increment => $"Increment · +{IncrementPerSecond:0.###} / s",
        GeneratorKind.RandomRange => $"Random · {Min:0.###}–{Max:0.###}",
        GeneratorKind.Sine => $"Sine · {SineMin:0.###}–{SineMax:0.###} · {PeriodSeconds:0.#} s",
        _ => "?",
    };

    /// <summary>Next scaled value at time t (UTC), advancing internal state by dt seconds.</summary>
    public double? Next(DateTime utcNow, double dtSeconds, double currentValue)
    {
        switch (Kind)
        {
            case GeneratorKind.Static:
                return null;

            case GeneratorKind.Increment:
                if (!_initialized)
                {
                    _current = currentValue;
                    _initialized = true;
                }

                _current += IncrementPerSecond * dtSeconds;
                if (_current > WrapMax) _current = WrapMin;
                return _current;

            case GeneratorKind.RandomRange:
                return Min + Random.Shared.NextDouble() * (Max - Min);

            case GeneratorKind.Sine:
            {
                double period = Math.Max(0.1, PeriodSeconds);
                double t = utcNow.Ticks / (double)TimeSpan.TicksPerSecond;
                double mid = (SineMin + SineMax) / 2;
                double amp = (SineMax - SineMin) / 2;
                return mid + amp * Math.Sin(_phaseOffset + t * Math.PI * 2 / period);
            }

            default:
                return null;
        }
    }
}

/// <summary>A slave-side tag: register definition + optional generator.</summary>
public sealed class SlaveTag
{
    public RegisterTag Tag { get; init; } = new();

    public ValueGenerator Generator { get; init; } = new();

    /// <summary>Marks values last changed by a remote master write (grid shows "written by client").</summary>
    public bool WrittenByClient { get; set; }
}
