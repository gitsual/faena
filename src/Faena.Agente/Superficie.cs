using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Faena.Escena;

namespace Faena.Agente;

/// <summary>
/// Lo que el agente maneja: una web en Playwright o una aplicación de escritorio por UI
/// Automation. El bucle de <see cref="Agent"/> no sabe cuál hay detrás: mira, decide, actúa y
/// apunta el paso que devuelve la superficie.
/// </summary>
public interface ISuperficie
{
    /// <summary>«web» o «escritorio»: cambia el prompt (no hay urls en una ventana) y el formato de los selectores.</summary>
    string Tipo { get; }
    /// <summary>Dónde está ahora: la url, o el título de la ventana.</summary>
    string Donde { get; }
    Task<string> TituloAsync();
    Task<Observation> ObservarAsync(CancellationToken ct);
    /// <summary>Hace la acción y devuelve el paso apuntado (formato de easy); null si el elemento no existe. Lanza si falla.</summary>
    Task<JsonObject?> ActuarAsync(Observation obs, Decision d, CancellationToken ct);
    /// <summary>Repite un paso ya apuntado (de una habilidad), por su selector duradero. Lanza si no se puede.</summary>
    Task RepetirAsync(JsonObject paso, CancellationToken ct);
    /// <summary>Deja que la interfaz se asiente tras actuar.</summary>
    Task AsentarAsync();
    /// <summary>Una captura PNG de lo que se ve, o null si no se puede.</summary>
    Task<byte[]?> CapturaAsync();
}

/// <summary>Una página de Playwright como superficie: lo que el agente hacía desde el principio.</summary>
public sealed class WebSuperficie(IPage page) : ISuperficie
{
    public IPage Page { get; } = page;
    public string Tipo => "web";
    public string Donde => Page.Url ?? "";

    public async Task<string> TituloAsync() => (await Page.TitleAsync().ConfigureAwait(false) ?? "").Trim();

    public Task<Observation> ObservarAsync(CancellationToken ct) => Observer.ObserveAsync(Page);

    public async Task<byte[]?> CapturaAsync()
    {
        try { return await Page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png }).ConfigureAwait(false); }
        catch (Exception) { return null; }
    }

    public async Task<JsonObject?> ActuarAsync(Observation obs, Decision d, CancellationToken ct)
    {
        var page = Page;
        if (d.Accion == "tecla")
        {
            var tecla = Palabras.Tecla(d.Valor);
            await page.Keyboard.PressAsync(tecla).ConfigureAwait(false);
            return new JsonObject { ["tipo"] = "tecla", ["valor"] = tecla, ["desc"] = Lengua.Tecla(tecla) };
        }
        if (d.Accion == "ir")
        {
            var url = d.Valor.Trim();
            await page.GotoAsync(url).ConfigureAwait(false);
            return new JsonObject { ["tipo"] = "ir", ["valor"] = url, ["desc"] = Lengua.Ve(url) };
        }
        var el = d.Ref is int n ? obs.Elemento(n) : null;
        if (el is null) return null;
        // El selector, antes de actuar: después del clic el elemento puede no estar.
        string selector;
        try { selector = await Observer.SelectorCompletoAsync(page, obs, el).ConfigureAwait(false); }
        catch (Exception) { selector = ""; }
        if (selector.Length == 0) selector = $"[data-faena-ref=\"{el.Ref}\"]";
        var locator = Observer.LocatorDe(page, obs, el);
        var nombre = el.Nombre;
        var cosa = Lengua.Cosa(el.Tipo, nombre);
        switch (d.Accion)
        {
            case "señalar":
            {
                // Se lleva el cursor encima sin pulsar: en el vídeo es un «point», y en vivo sirve para
                // comprobar que el elemento está ahí y a la vista.
                try { await locator.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 5000 }).ConfigureAwait(false); } catch (Exception) { }
                await locator.HoverAsync(new LocatorHoverOptions { Timeout = 10000 }).ConfigureAwait(false);
                return new JsonObject { ["tipo"] = "señalar", ["selector"] = selector, ["desc"] = Lengua.Senala(cosa) };
            }
            case "clic":
            {
                await locator.ClickAsync(new LocatorClickOptions { Timeout = 10000 }).ConfigureAwait(false);
                var marca = el.Tipo == "la casilla" ? (el.Estado.Contains("marcado") ? "off" : "on") : "";
                // El aviso de cookies sale la primera vez y luego ya no: ese clic es opcional.
                if (Palabras.Cookies.IsMatch(nombre) || Palabras.Cookies.IsMatch(selector))
                    return new JsonObject { ["tipo"] = "clic", ["selector"] = selector, ["opcional"] = true, ["desc"] = Lengua.Cookies(Lengua.Clic(cosa, marca)) };
                return new JsonObject { ["tipo"] = "clic", ["selector"] = selector, ["desc"] = Lengua.Clic(cosa, marca), ["guarda"] = Palabras.Guardan.IsMatch(nombre) };
            }
            case "escribir":
            {
                await locator.FillAsync(d.Valor, new LocatorFillOptions { Timeout = 10000 }).ConfigureAwait(false);
                // fill() solo no despierta a todos los frameworks; el evento input sí.
                try { await locator.DispatchEventAsync("input").ConfigureAwait(false); } catch (Exception) { }
                return new JsonObject { ["tipo"] = "escribir", ["selector"] = selector, ["valor"] = d.Valor, ["desc"] = Lengua.Escribe(d.Valor, nombre) };
            }
            case "elegir":
            {
                // El modelo ve el texto de las opciones; el paso guarda lo que Playwright acepte en «select».
                string elegido;
                try
                {
                    await locator.SelectOptionAsync(new SelectOptionValue { Label = d.Valor }, new LocatorSelectOptionOptions { Timeout = 10000 }).ConfigureAwait(false);
                    elegido = d.Valor;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    var valores = await locator.SelectOptionAsync(d.Valor, new LocatorSelectOptionOptions { Timeout = 10000 }).ConfigureAwait(false);
                    elegido = valores.Count != 0 ? valores[0] : d.Valor;
                }
                return new JsonObject { ["tipo"] = "elegir", ["selector"] = selector, ["valor"] = elegido, ["desc"] = Lengua.Elige(d.Valor, nombre.Length < 40 ? nombre : "") };
            }
            default:
                throw new ArgumentException($"unknown action “{d.Accion}”");
        }
    }

    /// <summary>
    /// Los pasos de una habilidad se repiten con el lenguaje de pasos de tramoya
    /// (<see cref="Steps.RunAsync"/> sobre una <see cref="EscenaWeb"/>), a ritmo de ensayo: las
    /// pausas se comprimen.
    /// </summary>
    public async Task RepetirAsync(JsonObject paso, CancellationToken ct)
    {
        var escenario = new EscenaWeb(Page, new Pacing(rehearse: true));
        Page.SetDefaultTimeout(Habilidades.Opcional(paso) ? 3000 : 10000);
        try { await Steps.RunAsync(escenario, Habilidades.Lineas(paso), _ => Task.CompletedTask).ConfigureAwait(false); }
        catch (Exception) when (Habilidades.Opcional(paso) && !ct.IsCancellationRequested)
        {
            // Un paso opcional (el aviso de cookies) que no sale no es un fallo.
        }
        finally { Page.SetDefaultTimeout(30000); }
    }

    /// <summary>
    /// Deja que la página se asiente tras actuar: un respiro para que arranque la navegación que
    /// provoca un Intro o un clic (si se mira antes, el contexto se destruye a medias), luego la
    /// carga y la red en calma, cada una con tope, y otro respiro para lo que pinta después.
    /// </summary>
    public async Task AsentarAsync()
    {
        await Task.Delay(300).ConfigureAwait(false);
        try { await Page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = 5000 }).ConfigureAwait(false); }
        catch (Exception) { /* sigue cargando: se mira igual */ }
        try { await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 }).ConfigureAwait(false); }
        catch (Exception) { /* una web que nunca se calla no es un fallo */ }
        await Task.Delay(300).ConfigureAwait(false);
    }
}
