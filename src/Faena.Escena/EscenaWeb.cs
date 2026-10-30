using Microsoft.Playwright;

namespace Faena.Escena;

/// <summary>
/// The step verbs of <see cref="IStage"/> over a Playwright page: what replays the steps of a
/// learned skill. It is tramoya's stage without what only a video needs (the ring cursor, the
/// caption band, the scene and wait marks): nobody watches a task, so the hand only has to
/// point, click and type where the steps say.
/// </summary>
public sealed class EscenaWeb : IStage
{
    public IPage Page { get; }
    public Pacing Pacing { get; }

    public EscenaWeb(IPage page, Pacing pacing)
    {
        Page = page;
        Pacing = pacing;
    }

    /// <summary>Pause for a beat, scaled by pacing (compressed when rehearsing).</summary>
    public async Task BeatAsync(double baseSeconds = 1.0) => await Pacing.SleepAsync(baseSeconds).ConfigureAwait(false);

    /// <summary>Scroll an element into view and move the mouse to its centre.</summary>
    public async Task PointAtAsync(string selector)
    {
        var locator = Page.Locator(selector).First;
        await locator.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
        var box = await locator.BoundingBoxAsync().ConfigureAwait(false);
        if (box is null) throw new InvalidOperationException($"element not visible: {selector}");
        await Page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 2, new MouseMoveOptions { Steps = 5 }).ConfigureAwait(false);
    }

    public async Task ClickAsync(string selector)
    {
        await PointAtAsync(selector).ConfigureAwait(false);
        await Page.Locator(selector).First.ClickAsync().ConfigureAwait(false);
    }

    public async Task ClickTextAsync(string text)
    {
        var locator = Page.GetByText(text, new PageGetByTextOptions { Exact = true }).First;
        await locator.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
        await locator.ClickAsync().ConfigureAwait(false);
    }

    /// <summary>Click the field and type into it key by key: some forms only listen to keystrokes.</summary>
    public async Task TypeIntoAsync(string selector, string text)
    {
        await ClickAsync(selector).ConfigureAwait(false);
        await Page.Locator(selector).First
            .PressSequentiallyAsync(text, new LocatorPressSequentiallyOptions { Delay = 10 })
            .ConfigureAwait(false);
    }

    public Task SelectAsync(string selector, string value) => Page.Locator(selector).First.SelectOptionAsync(value);

    /// <summary><c>fill("")</c> alone does not wake every framework; the <c>input</c> event does.</summary>
    public async Task ClearAsync(string selector)
    {
        await Page.FillAsync(selector, "").ConfigureAwait(false);
        await Page.DispatchEventAsync(selector, "input").ConfigureAwait(false);
    }

    public Task ScrollAsync(int dy) => Page.Mouse.WheelAsync(0, dy);

    public Task PressAsync(string key) => Page.Keyboard.PressAsync(key);

    public Task GotoAsync(string url) => Page.GotoAsync(url);

    /// <summary>Wait for something slow; not finding it is not an error here, the next step will say so if it mattered.</summary>
    public async Task WaitForAsync(string selector, double seconds)
    {
        try
        {
            await Page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = (float)(seconds * 1000),
            }).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
        }
    }
}
