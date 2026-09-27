using FamilyTogether.Core;
using FamilyTogether.Mobile.Localization;
using FamilyTogether.Mobile.Services;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace FamilyTogether.Mobile.Pages;

/// <summary>
/// La camara leyendo el QR de una invitacion (<c>familytogether://join?c=XXXX</c>), como en Task Manager.
/// Devuelve el codigo al que abrio la pantalla, que es quien sabe unirse y contar lo que pasa.
/// </summary>
public sealed class ScanQrPage : ContentPage
{
    /// <summary>Lo que se espera a que la camara empiece a dar imagen antes de sospechar.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(6);

    private readonly CameraBarcodeReaderView _camera = new();
    private readonly Label _hint = new() { HorizontalTextAlignment = TextAlignment.Center, Padding = new Thickness(20, 12) };
    private readonly TaskCompletionSource<string?> _result = new();
    private int _delivered;
    private int _frames;

    /// <summary>
    /// Lo leido, guardado antes de cerrar: al cerrar salta <see cref="OnNavigatedFrom"/>, que responde
    /// con esto. Al reves, la lectura buena llegaba tarde y se tiraba (fallo de Task Manager).
    /// </summary>
    private string? _read;

    private ScanQrPage()
    {
        Title = Loc.Get("ScanQr");
        _hint.Style = Ui.Style("HintText");
        _hint.Text = Loc.Get("ScanHint");

        ToolbarItems.Add(new ToolbarItem { IconImageSource = "ic_close_w.png", Text = Loc.Get("Close"), Command = new Command(async () => await CloseAsync()) });

        _camera.Options = new BarcodeReaderOptions
        {
            // Solo QR; mas fino y tambien en negativo: un QR en la pantalla de otro movil llega con
            // reflejos, torcido y a veces en claro sobre oscuro.
            Formats = BarcodeFormat.QrCode,
            AutoRotate = true,
            TryHarder = true,
            TryInverted = true,
            Multiple = false,
        };
        _camera.BarcodesDetected += OnBarcodesDetected;
        _camera.FrameReady += (_, _) => Interlocked.Increment(ref _frames);

        var root = new Grid { RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)] };
        root.Add(_camera, 0, 0);
        root.Add(_hint, 0, 1);
        Content = root;
    }

    /// <summary>
    /// Abre la camara y espera. Devuelve el codigo leido, o <c>null</c> si se cerro sin leer nada o
    /// si no hay permiso de camara (se dice, y se recuerda que el codigo se puede escribir).
    /// </summary>
    public static async Task<string?> RequestAsync(Page origin)
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();

        if (status != PermissionStatus.Granted)
        {
            await Ui.AlertAsync(origin, "ScanQr", Loc.Get("ScanNoCamera"));
            return null;
        }

        var page = new ScanQrPage();
        await origin.Navigation.PushAsync(page);
        return await page._result.Task;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Dispatcher.DispatchDelayed(Patience, () =>
        {
            if (Volatile.Read(ref _frames) == 0)
                _hint.Text = Loc.Get("ScanNoFrames");
        });
    }

    private void OnBarcodesDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        // Llega desde el hilo de la camara y varias veces con el mismo codigo: se atiende una.
        if (e.Results.Length == 0 || Interlocked.Exchange(ref _delivered, 1) == 1)
            return;

        var text = e.Results[0].Value;
        Dispatcher.Dispatch(async () =>
        {
            _camera.IsDetecting = false;

            if (!InviteCodes.TryParse(text, out var code))
            {
                // Otro QR cualquiera (una wifi, un ticket): se dice y se sigue mirando.
                await Ui.AlertAsync(this, "ScanQr", Loc.Get("ScanNotOurs"));
                Interlocked.Exchange(ref _delivered, 0);
                _camera.IsDetecting = true;
                return;
            }

            _read = code;
            await Navigation.PopAsync();
            _result.TrySetResult(code);
        });
    }

    private async Task CloseAsync()
    {
        _camera.IsDetecting = false;
        await Navigation.PopAsync();
    }

    /// <summary>Tambien se responde al salir con atras: el que espera no puede quedarse colgado.</summary>
    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        _result.TrySetResult(_read);
    }
}
