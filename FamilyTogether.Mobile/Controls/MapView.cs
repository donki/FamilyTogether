using System.Globalization;
using System.Text.Json;
using FamilyTogether.Mobile.Services;

namespace FamilyTogether.Mobile.Controls;

/// <summary>
/// El mapa: un WebView con <c>Resources\Raw\map.html</c> (MapLibre GL empaquetado). Mismo enfoque
/// que Hiker: se habla con el mapa llamando a sus funciones con <c>EvaluateJavaScriptAsync</c>.
/// </summary>
/// <remarks>
/// <para><b>Listo de verdad.</b> No se llama a nada hasta que la pagina contesta que sus funciones
/// existen (hasta 15 s). Con un WebView lento, lo que se pedia antes se perdia en silencio.</para>
///
/// <para><b>Toques.</b> El mapa guarda lo que se toca en una cola y aqui se recoge cada 400 ms
/// mientras la pagina esta visible (<see cref="StartEvents"/>). Es mas simple y mas fiable que
/// interceptar navegaciones a un esquema propio.</para>
/// </remarks>
public sealed class MapView : ContentView
{
    private readonly WebView _web = new() { BackgroundColor = Colors.Transparent };
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDispatcherTimer? _events;
    private bool _started;

    public MapView()
    {
        Content = _web;
        Loaded += (_, _) => Initialize();
    }

    /// <summary>Se toco el mapa (solo con <c>setTapEnabled(true)</c> o editando una zona).</summary>
    public event EventHandler<(double Lat, double Lon)>? MapTapped;

    /// <summary>Se toco el marcador de un miembro.</summary>
    public event EventHandler<Guid>? MemberTapped;

    public static string Json(object value) => JsonSerializer.Serialize(value);

    public static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Llama a una funcion del mapa cuando este listo. Un fallo se registra y no sube.</summary>
    public async Task RunAsync(string script)
    {
        Initialize();
        if (!await _ready.Task.ConfigureAwait(true))
            return;

        try
        {
            await _web.EvaluateJavaScriptAsync(script);
        }
        catch (Exception ex)
        {
            CrashLog.Error("MapView.RunAsync", ex);
        }
    }

    public void StartEvents()
    {
        _events ??= CreateEventTimer();
        _events.Start();
    }

    public void StopEvents() => _events?.Stop();

    private void Initialize()
    {
        if (_started)
            return;
        _started = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("map.html");
            using var reader = new StreamReader(stream);

            // Sin BaseUrl, MAUI carga el HTML con base file:///android_asset/: ahi estan
            // maplibre-gl.js y maplibre-gl.css (Resources\Raw).
            _web.Source = new HtmlWebViewSource { Html = await reader.ReadToEndAsync() };

            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(150);
                try
                {
                    var answer = await _web.EvaluateJavaScriptAsync("(window.mapReady === true) ? 'yes' : 'no'");
                    if (answer?.Trim('"') == "yes")
                    {
                        _ready.TrySetResult(true);
                        return;
                    }
                }
                catch (Exception)
                {
                    // El WebView aun no acepta JavaScript: se vuelve a preguntar.
                }
            }

            CrashLog.Info("MapView: el mapa no contesto en 15 s.");
            _ready.TrySetResult(false);
        }
        catch (Exception ex)
        {
            CrashLog.Error("MapView.LoadAsync", ex);
            _ready.TrySetResult(false);
        }
    }

    private IDispatcherTimer CreateEventTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.Tick += async (_, _) => await PollEventsAsync();
        return timer;
    }

    private bool _polling;

    private async Task PollEventsAsync()
    {
        if (_polling || !_ready.Task.IsCompleted || !_ready.Task.Result)
            return;

        _polling = true;
        try
        {
            var raw = (await _web.EvaluateJavaScriptAsync("takeEvents()"))?.Trim('"');
            if (string.IsNullOrEmpty(raw) || raw == "null")
                return;

            foreach (var ev in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = ev.Split('|');
                if (parts[0] == "tap" && parts.Length == 3
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                    && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                {
                    MapTapped?.Invoke(this, (lat, lon));
                }
                else if (parts[0] == "member" && parts.Length == 2 && Guid.TryParse(parts[1], out var id))
                {
                    MemberTapped?.Invoke(this, id);
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Error("MapView.PollEventsAsync", ex);
        }
        finally
        {
            _polling = false;
        }
    }
}
