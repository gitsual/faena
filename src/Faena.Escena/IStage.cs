namespace Faena.Escena;

/// <summary>
/// What the step language needs from a stage. <see cref="EscenaWeb"/> implements it over a
/// Playwright page; tests use a fake.
/// </summary>
public interface IStage
{
    Task ClickAsync(string selector);
    Task ClickTextAsync(string text);
    Task TypeIntoAsync(string selector, string text);
    Task SelectAsync(string selector, string value);
    /// <summary><c>page.fill(selector, "")</c> followed by an <c>input</c> event.</summary>
    Task ClearAsync(string selector);
    Task PointAtAsync(string selector);
    Task ScrollAsync(int dy);
    Task PressAsync(string key);
    Task GotoAsync(string url);
    Task BeatAsync(double seconds);
    /// <summary>Wait up to <paramref name="seconds"/> for <paramref name="selector"/> to be visible, inside a <c>wait:start</c>/<c>wait:end</c> window the montage can speed up.</summary>
    Task WaitForAsync(string selector, double seconds);
}
