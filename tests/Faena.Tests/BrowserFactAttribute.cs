namespace Faena.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for the tests that launch a real Chromium. They need the
/// Playwright browser installed (<c>playwright.ps1 install chromium</c>) and
/// <c>PLAYWRIGHT_BROWSERS_PATH</c> pointing at it, which the normal run cannot assume,
/// so they are opt-in: set <c>FAENA_BROWSER_TESTS=1</c> and they run; otherwise they
/// show up as skipped with the reason, never as green without having opened anything.
/// An environment variable rather than a constant so no source edit is needed to run
/// them, and the skip is decided per process, when xunit reads the attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BrowserFactAttribute : FactAttribute
{
    public const string Variable = "FAENA_BROWSER_TESTS";

    public BrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"set {Variable}=1 to run the browser tests";
        }
    }
}
