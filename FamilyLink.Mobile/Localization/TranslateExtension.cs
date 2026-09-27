namespace FamilyLink.Mobile.Localization;

/// <summary>
/// Texto traducido en XAML: <c>Text="{loc:T MenuMap}"</c>.
/// </summary>
/// <remarks>
/// Devuelve la cadena ya resuelta. Task Manager devolvia un enlace vivo al indexador, pero MAUI no
/// lo reevalua al cambiar de idioma (comprobado en tablet el 2026-08-30); aqui el cambio de idioma
/// reconstruye el Shell y cada pagina vuelve a pedir sus textos al construirse.
/// </remarks>
[ContentProperty(nameof(Key))]
[AcceptEmptyServiceProvider]
public sealed class TExtension : IMarkupExtension<string>
{
    public string Key { get; set; } = string.Empty;

    public string ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
