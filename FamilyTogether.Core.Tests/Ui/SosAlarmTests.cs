using FamilyTogether.Mobile.Services.Native;

namespace FamilyTogether.Core.Tests.Ui;

/// <summary>La alarma SOS aunque el movil este en silencio (<see cref="SosAlarmLogic"/>) con un aparato de mentira.</summary>
public class SosAlarmTests : IDisposable
{
    private sealed class FakeDevice : IAlarmDevice
    {
        public List<string> Calls { get; } = [];
        public bool HasTone { get; set; } = true;
        public int Volume { get; set; } = 3;
        public int Max { get; set; } = 7;
        public HashSet<string> Broken { get; } = [];
        public TimeSpan? StopIn { get; private set; }

        private void Call(string name)
        {
            Calls.Add(name);
            if (Broken.Contains(name))
                throw new InvalidOperationException(name + " roto");
        }

        public bool PlaySound() { Call("play"); return HasTone; }
        public void StopSound() => Call("stopSound");
        public void Vibrate() => Call("vibrate");
        public void StopVibration() => Call("stopVibration");
        public (int Now, int Max) AlarmVolume() { Call("volume"); return (Volume, Max); }
        public void SetAlarmVolume(int volume) { Call("setVolume"); Volume = volume; }
        public void StartKeepAlive() => Call("keepAlive");
        public void StopKeepAlive() => Call("stopKeepAlive");
        public void ScheduleStop(TimeSpan after) { Call("schedule"); StopIn = after; }
        public void CancelScheduledStop() { Call("cancel"); StopIn = null; }
    }

    private readonly List<string> _log = [];
    private readonly FakeDevice _device = new();
    private readonly SosAlarmLogic _alarm;

    public SosAlarmTests()
    {
        NativeLog.Logcat = (_, l) => _log.Add(l);
        _alarm = new SosAlarmLogic(_device);
    }

    public void Dispose() => NativeLog.Logcat = null;

    [Fact]
    public void SuenaAlMaximoUnMinutoYDejaElVolumenComoEstaba()
    {
        Assert.True(_alarm.Start());
        Assert.True(_alarm.IsRinging);
        Assert.Equal(7, _device.Volume);
        Assert.Equal(SosAlarmLogic.Duration, _device.StopIn);
        Assert.Equal(TimeSpan.FromMinutes(1), SosAlarmLogic.Duration);
        Assert.Equal(["volume", "setVolume", "play", "vibrate", "keepAlive", "schedule"], _device.Calls);

        // Otro SOS mientras suena: no vuelve a empezar el tono, solo alarga la cuenta.
        _device.Calls.Clear();
        Assert.True(_alarm.Start());
        Assert.Equal(["schedule"], _device.Calls);

        _device.Calls.Clear();
        _alarm.Stop();
        Assert.False(_alarm.IsRinging);
        Assert.Equal(3, _device.Volume);
        Assert.Equal(["cancel", "stopSound", "stopVibration", "volume", "setVolume", "stopKeepAlive"], _device.Calls);

        // Parar otra vez (Silenciar despues de abrir la app) no hace nada mas.
        _device.Calls.Clear();
        _alarm.Stop();
        Assert.DoesNotContain("stopKeepAlive", _device.Calls);
        Assert.DoesNotContain("setVolume", _device.Calls);
    }

    [Fact]
    public void ConElVolumenYaAlMaximoNoLoToca()
    {
        _device.Volume = 7;
        Assert.True(_alarm.Start());
        _alarm.Stop();
        Assert.DoesNotContain("setVolume", _device.Calls);
    }

    [Fact]
    public void SiElUsuarioCambiaElVolumenMientrasSuenaSeRespeta()
    {
        Assert.True(_alarm.Start());
        _device.Volume = 5;
        _alarm.Stop();
        Assert.Equal(5, _device.Volume);
    }

    [Fact]
    public void SinTonoNoSuenaYDevuelveElVolumen()
    {
        _device.HasTone = false;
        Assert.False(_alarm.Start());
        Assert.False(_alarm.IsRinging);
        Assert.Equal(3, _device.Volume);
        Assert.DoesNotContain("keepAlive", _device.Calls);
    }

    [Fact]
    public void SiElTonoFallaNoSuenaYLoDice()
    {
        _device.Broken.Add("play");
        Assert.False(_alarm.Start());
        Assert.False(_alarm.IsRinging);
        Assert.Equal(3, _device.Volume);
        Assert.Contains(_log, l => l.Contains("No se pudo hacer sonar la alarma SOS"));
    }

    [Fact]
    public void LoDemasPuedeFallarYSuenaIgual()
    {
        // Sin poder tocar el volumen, sin vibrador ni servicio: suena con lo que haya.
        foreach (var name in new[] { "setVolume", "vibrate", "keepAlive" })
            _device.Broken.Add(name);
        Assert.True(_alarm.Start());
        Assert.True(_alarm.IsRinging);

        foreach (var name in new[] { "cancel", "stopSound", "stopVibration", "stopKeepAlive" })
            _device.Broken.Add(name);
        _alarm.Stop();
        Assert.False(_alarm.IsRinging);
        Assert.Contains(_log, l => l.Contains("subir el volumen"));
        Assert.Contains(_log, l => l.Contains("vibrar"));
        Assert.Contains(_log, l => l.Contains("arrancar el servicio"));
        Assert.Contains(_log, l => l.Contains("parar el servicio"));
    }

    [Fact]
    public void SiNoSePuedeDevolverElVolumenLoDice()
    {
        Assert.True(_alarm.Start());
        _device.Broken.Add("volume");
        _alarm.Stop();
        Assert.Contains(_log, l => l.Contains("devolver el volumen"));
        Assert.False(_alarm.IsRinging);
    }
}
