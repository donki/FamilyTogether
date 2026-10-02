using System.Reflection;
using FamilyTogether.Mobile.Localization;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>
/// Manejo de las paginas sin pantalla: recorrer el arbol de vistas, pulsar botones por su texto,
/// contestar a los dialogos de <c>ModernDialog</c> y esperar a que termine el trabajo asincrono.
/// </summary>
internal static class UiDriver
{
    public static IEnumerable<Element> Descendants(this Element root)
    {
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            var children = e switch
            {
                ContentPage p when p.Content is not null => [p.Content],
                ScrollView s when s.Content is not null => [s.Content],
                ContentView c when c.Content is not null => [c.Content],
                Border b when b.Content is not null => [b.Content],
                Layout l => l.Children.OfType<Element>().ToArray(),
                _ => ((IElementController)e).LogicalChildren.ToArray(),
            };
            foreach (var c in children.Reverse())
                stack.Push(c);
        }
    }

    public static IEnumerable<T> All<T>(this Element root) where T : Element => root.Descendants().OfType<T>();

    public static IEnumerable<string> Texts(this Element root) =>
        root.Descendants().Select(e => e switch
        {
            Label l => l.Text ?? (l.FormattedText is { } f ? string.Concat(f.Spans.Select(s => s.Text)) : null),
            Button b => b.Text,
            _ => null,
        }).OfType<string>();

    public static bool Shows(this Element root, string text) => root.Texts().Any(t => t.Contains(text, StringComparison.Ordinal));

    /// <summary>Se ve: ella y todos sus contenedores estan visibles.</summary>
    public static bool Visible(this Element e)
    {
        for (var x = e; x is not null; x = x.Parent)
            if (x is VisualElement v && !v.IsVisible)
                return false;
        return true;
    }

    /// <summary>El boton con ese texto; si hay varios, el que se ve (y de ellos, el ultimo).</summary>
    public static Button Button(this Element root, string text) =>
        root.All<Button>().Where(b => b.Text == text).OrderBy(b => b.Visible()).LastOrDefault()
        ?? throw new InvalidOperationException($"No hay boton «{text}». Hay: {string.Join(" | ", root.All<Button>().Select(b => b.Text))}");

    public static Button ButtonKey(this Element root, string key) => root.Button(Loc.Get(key));

    public static void Click(this Element root, string text) => root.Button(text).SendClicked();

    public static void ClickKey(this Element root, string key) => root.ButtonKey(key).SendClicked();

    public static void Tap(this View view)
    {
        foreach (var g in view.GestureRecognizers.OfType<TapGestureRecognizer>())
            typeof(TapGestureRecognizer).GetMethod("SendTapped", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .Invoke(g, g.GetType().GetMethod("SendTapped", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetParameters().Length == 2
                    ? [view, null] : [view]);
    }

    public static void Toolbar(this Page page, string key)
    {
        var item = page.ToolbarItems.First(t => t.Text == Loc.Get(key));
        item.Command.Execute(item.CommandParameter);
    }

    /// <summary>El dialogo abierto (<c>ModernDialog</c>), si lo hay.</summary>
    public static Grid? Dialog(this ContentPage page) =>
        page.Content is Grid g ? g.Children.OfType<Grid>().LastOrDefault(c => c.StyleId == "__modernDialogOverlay") : null;

    /// <summary>Espera a que salga un dialogo y pulsa el boton con ese texto.</summary>
    public static async Task AnswerAsync(this ContentPage page, string button)
    {
        await Until(() => page.Dialog() is { } d && d.All<Button>().Any(b => b.Text == button), $"dialogo con «{button}»");
        var dialog = page.Dialog()!;
        dialog.All<Button>().First(b => b.Text == button).SendClicked();
        await Until(() => dialog.Parent is null, "cerrar el dialogo");
    }

    public static Task AnswerKeyAsync(this ContentPage page, string key) => page.AnswerAsync(Loc.Get(key));

    /// <summary>Texto del dialogo abierto (titulo y mensaje).</summary>
    public static async Task<string> DialogTextAsync(this ContentPage page)
    {
        await Until(() => page.Dialog() is not null, "un dialogo");
        return string.Join("\n", page.Dialog()!.All<Label>().Select(l => l.Text));
    }

    /// <summary>Escribe en la casilla del dialogo de texto y acepta.</summary>
    public static async Task PromptAsync(this ContentPage page, string text, string acceptKey)
    {
        await Until(() => page.Dialog()?.All<Entry>().Any() == true, "dialogo con casilla");
        page.Dialog()!.All<Entry>().First().Text = text;
        await page.AnswerKeyAsync(acceptKey);
    }

    public static async Task Until(Func<bool> condition, string what = "la condicion", int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException("No se cumplio: " + what);
            await Task.Delay(10);
        }
    }

    /// <summary>Deja correr lo asincrono pendiente (continuaciones en el grupo de hilos).</summary>
    public static Task Settle(int ms = 60) => Task.Delay(ms);

    public static void Appear(this Page page) =>
        typeof(Page).GetMethod("SendAppearing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(page, null);

    public static void Disappear(this Page page) =>
        typeof(Page).GetMethod("SendDisappearing", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(page, null);

    /// <summary>Pone la pagina en una ventana con navegacion, como si el Shell la hubiera abierto.</summary>
    public static NavigationPage Host(this Page page)
    {
        var nav = new NavigationPage(page);
        var window = new Window(nav);
        Application.Current!.AddWindowForTests(window);
        return nav;
    }

    private static void AddWindowForTests(this Application app, Window window) =>
        typeof(Application).GetMethod("AddWindow", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(Window)])!.Invoke(app, [window]);
}

internal static class UiDriverIcons
{
    /// <summary>Boton de icono por su descripcion para el lector de pantalla.</summary>
    public static ImageButton Icon(this Element root, string description) =>
        root.All<ImageButton>().FirstOrDefault(b => SemanticProperties.GetDescription(b) == description)
        ?? throw new InvalidOperationException($"No hay icono «{description}»");

    public static void ClickIcon(this Element root, string description) => ((IButtonController)root.Icon(description)).SendClicked();

    public static void ClickIconKey(this Element root, string key) => root.ClickIcon(FamilyTogether.Mobile.Localization.Loc.Get(key));
}
