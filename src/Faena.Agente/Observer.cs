using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Faena.Agente;

/// <summary>Un elemento con el que se puede actuar, tal como lo ve el modelo: número, tipo, nombre, valor y estado.</summary>
/// <param name="Ref">El número que le ha tocado en esta observación (vuelve a empezar en cada turno).</param>
/// <param name="Frame">El camino hasta su marco en forma de selector (vacío en la página principal), como en el grabador de <c>easy</c>.</param>
/// <param name="Tipo">«el botón», «el enlace», «el campo»... las mismas palabras que usa <c>escucha.js</c>.</param>
/// <param name="Nombre">El nombre visible (etiqueta, texto, placeholder), recortado.</param>
/// <param name="Valor">Lo que hay escrito o elegido, si es un campo o un desplegable.</param>
/// <param name="Estado">«marcado», «abierto», «seleccionado», «deshabilitado» o vacío.</param>
public sealed record ObservedElement(int Ref, string Frame, string Tipo, string Nombre, string Valor, string Estado);

/// <summary>Lo que el modelo ve de la página en un turno: dónde está y con qué puede actuar.</summary>
public sealed class Observation
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Los primeros 1500 caracteres de texto visible, para que sepa de qué va la página.</summary>
    public string Text { get; set; } = "";
    public List<ObservedElement> Elements { get; set; } = [];
    /// <summary>Cuántos elementos había de verdad: si es más que <see cref="Elements"/>, la lista está recortada.</summary>
    public int Total { get; set; }
    /// <summary>El marco de cada elemento que no está en la página principal (clave: el número).</summary>
    public Dictionary<int, IFrame> Marcos { get; } = [];

    public ObservedElement? Elemento(int @ref) => Elements.FirstOrDefault(e => e.Ref == @ref);
}

/// <summary>
/// Los ojos del agente: numera los elementos interactivos visibles de la página (y de sus
/// marcos) con <c>data-faena-ref</c> y devuelve una lista compacta para el modelo. Los
/// nombres de tipo son los mismos que apunta <c>escucha.js</c> cuando graba una persona, para
/// que los pasos del agente se lean igual que los grabados a mano.
/// </summary>
public static class Observer
{
    /// <summary>Tope de elementos que se le enseñan al modelo: más no caben en su contexto ni le ayudan.</summary>
    public const int MaxElementos = 150;
    /// <summary>Cuánto texto visible se le enseña: primero el que está en pantalla, luego el resto de la página.</summary>
    public const int MaxTexto = 2000;

    /// <summary>
    /// Se ejecuta en cada marco con <c>{offset, max}</c>: quita los números del turno anterior,
    /// recorre los elementos con los que se puede actuar, descarta los que no se ven y numera
    /// desde <c>offset</c>. Los que están dentro de la ventana van primero: son los que la persona
    /// vería y los que casi siempre importan; el resto se numera después hasta el tope.
    /// </summary>
    public const string ObserveJs = """
        (arg) => {
          const offset = arg.offset, max = arg.max;
          const ROLES = {button: "el botón", link: "el enlace", tab: "la pestaña", menuitem: "la opción del menú",
                         option: "la opción", checkbox: "la casilla", radio: "la opción", gridcell: "la celda",
                         cell: "la celda", treeitem: "el elemento", switch: "el interruptor", combobox: "el desplegable",
                         textbox: "el campo", searchbox: "el buscador"};
          const SEL = "button, a[href], input, select, textarea, summary, label, [onclick], [role=button], [role=link], " +
                      "[role=tab], [role=menuitem], [role=option], [role=checkbox], [role=radio], [role=switch], " +
                      "[role=combobox], [role=treeitem], [role=gridcell]";
          document.querySelectorAll("[data-faena-ref]").forEach(e => e.removeAttribute("data-faena-ref"));
          const limpio = (t, n) => (t || "").replace(/\s+/g, " ").trim().slice(0, n || 60);
          const tipoInput = (el) => (el.type || "").toLowerCase();
          const campo = (el) => el.tagName === "TEXTAREA" || el.tagName === "SELECT" ||
            (el.tagName === "INPUT" && !["checkbox", "radio", "button", "submit", "reset", "image", "hidden", "file"].includes(tipoInput(el)));
          const tipoDe = (el) => {
            const r = el.getAttribute("role");
            if (r && ROLES[r]) return ROLES[r];
            if (el.tagName === "INPUT") {
              const t = tipoInput(el);
              if (t === "checkbox") return "la casilla";
              if (t === "radio") return "la opción";
              if (["button", "submit", "reset", "image"].includes(t)) return "el botón";
              if (t === "search") return "el buscador";
              return "el campo";
            }
            if (el.tagName === "TEXTAREA") return "el campo";
            if (el.tagName === "SELECT") return "el desplegable";
            if (el.tagName === "BUTTON" || el.tagName === "SUMMARY") return "el botón";
            if (el.tagName === "A") return "el enlace";
            if (el.tagName === "LABEL") return "la etiqueta";
            return "el elemento";
          };
          const etiqueta = (el) => {
            const ids = el.getAttribute("aria-labelledby");
            if (ids) { const t = ids.split(/\s+/).map(i => { const n = document.getElementById(i); return n ? n.innerText : ""; }).join(" "); if (t.trim()) return t; }
            return (el.labels && el.labels[0] && el.labels[0].innerText) || "";
          };
          const nombre = (el) => {
            const img = el.querySelector && el.querySelector("img[alt]");
            if (campo(el))
              return limpio(el.getAttribute("aria-label") || etiqueta(el) || el.placeholder || el.title || el.name || el.id);
            return limpio(el.getAttribute("aria-label") || etiqueta(el) || el.innerText || el.value || el.title
                          || (img && img.alt) || el.alt || el.name || (el.tagName === "A" ? el.getAttribute("href") : ""));
          };
          const valor = (el) => {
            if (el.tagName === "SELECT") { const o = el.options[el.selectedIndex]; return limpio(o && o.text); }
            if (campo(el)) return tipoInput(el) === "password" ? (el.value ? "••••" : "") : limpio(el.value);
            return "";
          };
          const estado = (el) => {
            const s = [];
            const t = tipoInput(el);
            if ((el.tagName === "INPUT" && (t === "checkbox" || t === "radio") && el.checked) || el.getAttribute("aria-checked") === "true") s.push("marcado");
            if (el.getAttribute("aria-expanded") === "true" || (el.tagName === "SUMMARY" && el.parentElement && el.parentElement.open)) s.push("abierto");
            if (el.getAttribute("aria-selected") === "true" || el.getAttribute("aria-current") || el.getAttribute("aria-pressed") === "true") s.push("seleccionado");
            if (el.disabled || el.getAttribute("aria-disabled") === "true") s.push("deshabilitado");
            return s.join(", ");
          };
          const visible = (el) => {
            if (el.tagName === "INPUT" && tipoInput(el) === "hidden") return false;
            if (el.tagName === "LABEL" && el.control) return false;   // el control ya sale por su cuenta
            const r = el.getBoundingClientRect();
            if (r.width < 2 || r.height < 2) return false;
            const cs = getComputedStyle(el);
            if (cs.visibility === "hidden" || cs.display === "none" || cs.opacity === "0") return false;
            return true;
          };
          const enVentana = (el) => {
            const r = el.getBoundingClientRect();
            return r.bottom > 0 && r.right > 0 && r.top < innerHeight && r.left < innerWidth;
          };
          // El texto que la persona tiene delante va primero: la respuesta a «¿dónde está…?» suele
          // estar escrita al lado de lo que se pregunta. El resto de la página, después, hasta el tope.
          const textoEnPantalla = () => {
            const trozos = [];
            const w = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_TEXT);
            let n;
            while ((n = w.nextNode())) {
              const t = (n.nodeValue || "").replace(/\s+/g, " ").trim();
              if (!t) continue;
              const p = n.parentElement;
              if (!p || ["SCRIPT", "STYLE", "NOSCRIPT", "TEMPLATE"].includes(p.tagName)) continue;
              const r = p.getBoundingClientRect();
              if (r.width < 1 || r.height < 1 || r.bottom <= 0 || r.top >= innerHeight || r.right <= 0 || r.left >= innerWidth) continue;
              const cs = getComputedStyle(p);
              if (cs.visibility === "hidden" || cs.display === "none") continue;
              trozos.push(t);
              if (trozos.join(" ").length > 2000) break;
            }
            return trozos.join(" ");
          };
          const todos = Array.from(document.querySelectorAll(SEL)).filter(visible);
          const dentro = todos.filter(enVentana), fuera = todos.filter(e => !enVentana(e));
          const orden = dentro.concat(fuera);
          const elementos = [];
          orden.slice(0, Math.max(0, max)).forEach((el, i) => {
            el.setAttribute("data-faena-ref", String(offset + i));
            elementos.push({ref: offset + i, tipo: tipoDe(el), nombre: nombre(el), valor: valor(el), estado: estado(el)});
          });
          const pantalla = textoEnPantalla();
          const resto = limpio(document.body && document.body.innerText, 2000);
          const texto = pantalla.length !== 0 ? limpio(pantalla, 2000) + (resto.length > pantalla.length ? " … " + resto.slice(0, 2000 - Math.min(2000, pantalla.length)) : "") : resto;
          return JSON.stringify({url: location.href, title: document.title, text: limpio(texto, 2000),
                                 total: todos.length, elementos: elementos});
        }
        """;

    /// <summary>
    /// El selector duradero del elemento numerado: el que genera el propio Playwright
    /// (<c>window.playwright.selector</c>, que existe con <c>PWDEBUG=console</c>), por rol y
    /// nombre visible, como en el grabador de <c>easy</c>. Sin él, un CSS mínimo.
    /// </summary>
    public const string SelectorJs = """
        (n) => {
          const el = document.querySelector('[data-faena-ref="' + n + '"]');
          if (!el) return "";
          try { if (window.playwright && window.playwright.selector) return window.playwright.selector(el); } catch (e) {}
          if (el.id) return "#" + CSS.escape(el.id);
          if (el.name) return el.tagName.toLowerCase() + '[name="' + el.name + '"]';
          const t = (el.innerText || "").replace(/\s+/g, " ").trim().slice(0, 40);
          if (t) return "text=" + t;
          return el.tagName.toLowerCase();
        }
        """;

    /// <summary>Mira la página entera (marcos incluidos) y numera lo que se puede pulsar o rellenar.</summary>
    public static async Task<Observation> ObserveAsync(IPage page)
    {
        var obs = new Observation();
        // La página principal primero: su url y su título son los de la observación.
        var principal = await LeerAsync(page.EvaluateAsync<string>(ObserveJs, new { offset = 1, max = MaxElementos })).ConfigureAwait(false);
        obs.Url = principal.Url;
        obs.Title = principal.Title;
        obs.Text = principal.Text;
        obs.Total = principal.Total;
        obs.Elements.AddRange(principal.Elementos.Select(e => e with { Frame = "" }));
        // Las webs con marcos (muy típicas en aplicaciones de empresa): se numeran a continuación.
        IReadOnlyList<IFrame> marcos;
        try { marcos = page.Frames?.ToList() ?? []; }
        catch (Exception) { marcos = []; }
        foreach (var marco in marcos)
        {
            if (marco.ParentFrame is null) continue;
            var hueco = MaxElementos - obs.Elements.Count;
            string prefijo;
            try { prefijo = await PrefijoMarcoAsync(marco).ConfigureAwait(false); }
            catch (Exception) { prefijo = ""; }
            Parcial parcial;
            try
            {
                parcial = await LeerAsync(marco.EvaluateAsync<string>(ObserveJs, new { offset = obs.Elements.Count + 1, max = Math.Max(0, hueco) })).ConfigureAwait(false);
            }
            catch (Exception)
            {
                continue; // un marco de otro dominio o que se ha ido mientras mirábamos
            }
            obs.Total += parcial.Total;
            if (obs.Text.Length < MaxTexto && parcial.Text.Length != 0)
                obs.Text = Recortar(obs.Text + "\n" + parcial.Text, MaxTexto);
            var etiqueta = prefijo.Length != 0 ? prefijo : (marco.Name.Length != 0 ? marco.Name : marco.Url);
            foreach (var e in parcial.Elementos)
            {
                obs.Elements.Add(e with { Frame = etiqueta });
                obs.Marcos[e.Ref] = marco;
            }
        }
        return obs;
    }

    /// <summary>El selector duradero del elemento <paramref name="ref"/> en su marco (sin el prefijo del marco).</summary>
    public static Task<string> SelectorDuraderoAsync(IFrame frame, int @ref) => frame.EvaluateAsync<string>(SelectorJs, @ref);

    /// <summary>Lo mismo para la página principal.</summary>
    public static Task<string> SelectorDuraderoAsync(IPage page, int @ref) => page.EvaluateAsync<string>(SelectorJs, @ref);

    /// <summary>El selector completo de un elemento observado: el camino del marco y el selector duradero.</summary>
    public static async Task<string> SelectorCompletoAsync(IPage page, Observation obs, ObservedElement el)
    {
        var selector = obs.Marcos.TryGetValue(el.Ref, out var marco)
            ? await SelectorDuraderoAsync(marco, el.Ref).ConfigureAwait(false)
            : await SelectorDuraderoAsync(page, el.Ref).ConfigureAwait(false);
        if (string.IsNullOrEmpty(selector)) return "";
        return (marco is null ? "" : el.Frame) + selector;
    }

    /// <summary>El localizador con el que se actúa sobre el elemento numerado, en su marco.</summary>
    public static ILocator LocatorDe(IPage page, Observation obs, ObservedElement el)
    {
        var css = $"[data-faena-ref=\"{el.Ref}\"]";
        return obs.Marcos.TryGetValue(el.Ref, out var marco) ? marco.Locator(css) : page.Locator(css);
    }

    /// <summary>El camino hasta un marco como prefijo de selector, igual que <c>Engine.PrefijoMarcoAsync</c> en <c>easy</c>.</summary>
    public static async Task<string> PrefijoMarcoAsync(IFrame frame)
    {
        var partes = new List<string>();
        while (frame.ParentFrame is not null)
        {
            var elemento = await frame.FrameElementAsync().ConfigureAwait(false);
            partes.Insert(0, await elemento.EvaluateAsync<string>("e => window.playwright.selector(e)").ConfigureAwait(false));
            frame = frame.ParentFrame;
        }
        return string.Concat(partes.Select(p => $"{p} >> internal:control=enter-frame >> "));
    }

    private sealed record Parcial(string Url, string Title, string Text, int Total, List<ObservedElement> Elementos);

    private static async Task<Parcial> LeerAsync(Task<string> tarea)
    {
        var json = await tarea.ConfigureAwait(false);
        var nodo = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
        if (nodo is null) return new("", "", "", 0, []);
        var elementos = new List<ObservedElement>();
        if (nodo["elementos"] is JsonArray lista)
            foreach (var item in lista.OfType<JsonObject>())
                elementos.Add(new ObservedElement(Entero(item["ref"]), "", Str(item, "tipo"), Str(item, "nombre"), Str(item, "valor"), Str(item, "estado")));
        return new(Str(nodo, "url"), Str(nodo, "title"), Str(nodo, "text"), Entero(nodo["total"]), elementos);
    }

    internal static string Str(JsonObject o, string clave) => o[clave] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[clave]?.ToString() ?? "";

    internal static int Entero(JsonNode? nodo)
    {
        if (nodo is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), out i)) return i;
        return 0;
    }

    internal static string Recortar(string s, int n) => s.Length <= n ? s : s[..n];
}
