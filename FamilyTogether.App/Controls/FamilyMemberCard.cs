using FamilyTogether.App.Models;

namespace FamilyTogether.App.Controls;

public class FamilyMemberCard : ContentView
{
    public static readonly BindableProperty LocationUpdateProperty =
        BindableProperty.Create(nameof(LocationUpdate), typeof(LocationUpdate), typeof(FamilyMemberCard), null, propertyChanged: OnLocationUpdateChanged);

    public LocationUpdate LocationUpdate
    {
        get => (LocationUpdate)GetValue(LocationUpdateProperty);
        set => SetValue(LocationUpdateProperty, value);
    }

    private Label _nameLabel;
    private Label _timeLabel;
    private Label _statusLabel;
    private Frame _statusFrame;

    public FamilyMemberCard()
    {
        CreateUI();
    }

    private void CreateUI()
    {
        _nameLabel = new Label
        {
            FontSize = 16,
            FontAttributes = FontAttributes.Bold,
            TextColor = Application.Current?.RequestedTheme == AppTheme.Dark ? Colors.White : Color.FromArgb("#212121")
        };

        _timeLabel = new Label
        {
            FontSize = 12,
            TextColor = Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#BDBDBD") : Color.FromArgb("#757575")
        };

        _statusLabel = new Label
        {
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };

        _statusFrame = new Frame
        {
            CornerRadius = 12,
            Padding = new Thickness(8, 4),
            HasShadow = false,
            Content = _statusLabel
        };

        var mainLayout = new Grid
        {
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            },
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Padding = new Thickness(16, 12)
        };

        mainLayout.Add(_nameLabel, 0, 0);
        mainLayout.Add(_timeLabel, 0, 1);
        mainLayout.Add(_statusFrame, 1, 0);
        Grid.SetRowSpan(_statusFrame, 2);

        var cardFrame = new Frame
        {
            BackgroundColor = Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#424242") : Color.FromArgb("#FAFAFA"),
            CornerRadius = 12,
            HasShadow = false,
            Padding = 0,
            Margin = new Thickness(8, 4),
            Content = mainLayout,
            Shadow = new Shadow
            {
                Brush = Application.Current?.RequestedTheme == AppTheme.Dark ? Colors.Black : Color.FromArgb("#212121"),
                Opacity = 0.1f,
                Radius = 8,
                Offset = new Point(0, 2)
            }
        };

        Content = cardFrame;
    }

    private static void OnLocationUpdateChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is FamilyMemberCard card && newValue is LocationUpdate location)
        {
            card.UpdateUI(location);
        }
    }

    private void UpdateUI(LocationUpdate location)
    {
        if (location == null) return;

        _nameLabel.Text = location.UserName;
        _timeLabel.Text = GetTimeAgoText(location.MinutesAgo);

        // Actualizar estado con colores Material Design
        if (location.MinutesAgo <= 5)
        {
            _statusLabel.Text = "Activo";
            _statusFrame.BackgroundColor = Color.FromArgb("#4CAF50"); // Material Green
        }
        else if (location.MinutesAgo <= 30)
        {
            _statusLabel.Text = "Reciente";
            _statusFrame.BackgroundColor = Color.FromArgb("#FF9800"); // Material Orange
        }
        else
        {
            _statusLabel.Text = "Inactivo";
            _statusFrame.BackgroundColor = Color.FromArgb("#F44336"); // Material Red
        }

        // Añadir animación suave
        AnimateStatusUpdate();
    }

    private async void AnimateStatusUpdate()
    {
        await _statusFrame.ScaleTo(1.1, 100);
        await _statusFrame.ScaleTo(1.0, 100);
    }

    private string GetTimeAgoText(int minutesAgo)
    {
        if (minutesAgo < 1)
            return "Ahora";
        else if (minutesAgo < 60)
            return $"hace {minutesAgo} min";
        else if (minutesAgo < 1440) // 24 horas
            return $"hace {minutesAgo / 60} h";
        else
            return $"hace {minutesAgo / 1440} días";
    }
}