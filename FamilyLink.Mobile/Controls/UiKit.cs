using FamilyLink.Mobile.Services;

namespace FamilyLink.Mobile.Controls;

/// <summary>
/// Piezas de interfaz con los estilos de <c>Styles.xaml</c>, para las pantallas que se montan en
/// codigo (listas que dependen de los datos). Ningun boton con texto lleva altura fija.
/// </summary>
public static class UiKit
{
    public static Border Card(View content, Thickness? padding = null) => new()
    {
        Style = Ui.Style("Card"),
        Content = new ContentView { Padding = padding ?? new Thickness(16), Content = content },
    };

    public static Label Title(string text) => new() { Text = text, Style = Ui.Style("CardTitle"), LineBreakMode = LineBreakMode.WordWrap };

    public static Label Body(string text) => new() { Text = text, Style = Ui.Style("BodyText"), LineBreakMode = LineBreakMode.WordWrap };

    public static Label Hint(string text) => new() { Text = text, Style = Ui.Style("HintText"), LineBreakMode = LineBreakMode.WordWrap };

    public static Label ItemTitle(string text) => new() { Text = text, Style = Ui.Style("ItemTitle") };

    public static Label ItemSubtitle(string text) => new() { Text = text, Style = Ui.Style("ItemSubtitle") };

    public static Button Primary(string text, string? icon, EventHandler onClick) => Make("PrimaryButton", text, icon, onClick);

    public static Button Outline(string text, string? icon, EventHandler onClick) => Make("OutlineButton", text, icon, onClick);

    public static Button Danger(string text, string? icon, EventHandler onClick) => Make("DangerButton", text, icon, onClick);

    public static ImageButton Icon(string icon, EventHandler onClick, string? description = null)
    {
        var button = new ImageButton { Source = icon, Style = Ui.Style("RowIconButton") };
        if (description is not null)
            SemanticProperties.SetDescription(button, description);
        button.Clicked += onClick;
        return button;
    }

    public static BoxView Separator() => new() { Style = Ui.Style("Separator") };

    /// <summary>Caja con borde para una casilla de texto.</summary>
    public static Border Input(View entry) => new() { Style = Ui.Style("InputBox"), Content = entry };

    /// <summary>Fila con un texto que ocupa lo que haya y algo a la derecha (interruptor, boton).</summary>
    public static Grid Row(View left, View right)
    {
        var grid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            ColumnSpacing = 8,
        };
        left.VerticalOptions = LayoutOptions.Center;
        right.VerticalOptions = LayoutOptions.Center;
        grid.Add(left, 0, 0);
        grid.Add(right, 1, 0);
        return grid;
    }

    /// <summary>Banda de aviso con icono, texto y, si hace falta, un boton.</summary>
    public static Border Banner(string text, Button? action = null)
    {
        var stack = new VerticalStackLayout { Spacing = 6 };
        var head = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(new GridLength(24)), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 10,
        };
        head.Add(new Image { Source = "ic_warning.png", WidthRequest = 22, HeightRequest = 22, VerticalOptions = LayoutOptions.Start }, 0, 0);
        head.Add(Body(text), 1, 0);
        stack.Add(head);
        if (action is not null)
            stack.Add(action);
        return new Border { Style = Ui.Style("WarningBanner"), Content = stack };
    }

    /// <summary>Estructura de pagina: contenido que se desplaza y, si hay, botones fijos abajo.</summary>
    public static Grid Page(View scrollContent, View? bottom = null)
    {
        var root = new Grid
        {
            RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)],
        };
        root.Add(new ScrollView { Content = scrollContent }, 0, 0);
        if (bottom is not null)
            root.Add(new ContentView { Padding = new Thickness(16, 8, 16, 16), Content = bottom }, 0, 1);
        return root;
    }

    private static Button Make(string style, string text, string? icon, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            Style = Ui.Style(style),
            LineBreakMode = LineBreakMode.WordWrap,
            HorizontalOptions = LayoutOptions.Fill,
        };
        if (icon is not null)
        {
            button.ImageSource = icon;
            button.ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 8);
        }
        button.Clicked += onClick;
        return button;
    }
}
