namespace Faena.Escena;

/// <summary>
/// How fast a replay moves. <c>Pace</c> scales
/// every sleep; <c>Rehearse</c> compresses waits so a rehearsal finishes in seconds
/// while <see cref="TotalBase"/> still tracks the real, unscaled budget.
/// The sleep is injectable so tests never block.
/// </summary>
public sealed class Pacing
{
    private const double RehearseFactor = 0.05;
    private const double RehearseCapSeconds = 1.0;

    private readonly Func<double, Task> _sleep;

    public double Pace { get; }
    public bool Rehearse { get; }
    public string Lang { get; }
    public double TotalBase { get; private set; }

    public Pacing(double pace = 1.0, bool rehearse = false, string lang = "en", Func<double, Task>? sleep = null)
    {
        Pace = pace;
        Rehearse = rehearse;
        Lang = lang;
        _sleep = sleep ?? (s => s > 0 ? Task.Delay(TimeSpan.FromSeconds(s)) : Task.CompletedTask);
    }

    /// <summary>Sleep <paramref name="baseSeconds"/> scaled by pace (compressed when rehearsing). Returns the seconds actually slept.</summary>
    public async Task<double> SleepAsync(double baseSeconds)
    {
        TotalBase += baseSeconds;
        var actual = Rehearse
            ? Math.Min(baseSeconds, RehearseCapSeconds) * RehearseFactor
            : baseSeconds * Pace;
        await _sleep(actual).ConfigureAwait(false);
        return actual;
    }

    /// <summary>How long to wait for an operation; scales with pace, short in rehearsal.</summary>
    public double OpTimeout(double minimum = 20.0)
    {
        var scaled = Rehearse ? 5.0 : 12.0 * Pace;
        return Math.Max(minimum, scaled);
    }
}
