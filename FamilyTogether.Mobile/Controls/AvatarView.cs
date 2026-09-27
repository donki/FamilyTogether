using Microsoft.Maui.Controls.Shapes;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Controls;

/// <summary>
/// El avatar de una persona en un circulo: su foto si la tiene y, si no, sus iniciales sobre su
/// color (el mismo que su marcador en el mapa).
/// </summary>
public sealed class AvatarView : Border
{
    private readonly Image _image = new() { Aspect = Aspect.AspectFill };
    private readonly Label _initials = new()
    {
        TextColor = Colors.White,
        FontAttributes = FontAttributes.Bold,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
    };

    public AvatarView(double size = 40)
    {
        WidthRequest = size;
        HeightRequest = size;
        Padding = 0;
        StrokeThickness = 0;
        StrokeShape = new RoundRectangle { CornerRadius = size / 2 };
        VerticalOptions = LayoutOptions.Center;
        _initials.FontSize = Math.Max(12, size * 0.38);
        Content = new Grid { Children = { _initials, _image } };
    }

    public void Set(Guid user, string? name, string? avatarBase64)
    {
        BackgroundColor = Color.FromArgb(Avatars.ColorFor(user));
        _initials.Text = Avatars.Initials(name);
        _image.Source = Avatars.ToImage(avatarBase64);
        _image.IsVisible = _image.Source is not null;
    }
}
