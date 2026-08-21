using Jint;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Scripting;
using System.Diagnostics;

namespace RDM.Infrastructure.Scripting;

/// <summary>
/// Silnik skryptowy oparty na Jint (JavaScript ECMAScript 2020+).
/// Każde wykonanie tworzy nową, izolowaną instancję Engine.
/// Sandboxing: brak dostępu do System.*, brak require/import, limit pamięci 4MB, timeout 5s.
/// </summary>
public sealed class JintScriptEngine : IScriptEngine
{
    private const int MaxMemoryBytes    = 4 * 1024 * 1024;  // 4 MB
    private const int TimeoutSeconds    = 5;

    private readonly IScriptingFacade        _facade;
    private readonly ILogger<JintScriptEngine> _logger;

    public JintScriptEngine(IScriptingFacade facade, ILogger<JintScriptEngine> logger)
    {
        _facade = facade;
        _logger = logger;
    }

    public Task<ScriptResult> RunAsync(string scriptBody, CancellationToken ct = default)
    {
        return Task.Run(() => Execute(scriptBody, ct), ct);
    }

    private ScriptResult Execute(string scriptBody, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var engine = BuildEngine(ct);
            RegisterApis(engine);
            engine.Execute(scriptBody);

            sw.Stop();
            _logger.LogDebug("Skrypt wykonano w {Ms}ms", sw.ElapsedMilliseconds);
            return ScriptResult.Ok(sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return ScriptResult.Fail("Skrypt anulowany (timeout lub przerwanie)");
        }
        catch (Jint.Runtime.JavaScriptException jsEx)
        {
            _logger.LogWarning("Błąd JS: {Error} (linia {Line})", jsEx.Message, jsEx.Location.Start.Line);
            return ScriptResult.Fail($"JS error linia {jsEx.Location.Start.Line}: {jsEx.Message}");
        }
        catch (Jint.Runtime.StatementsCountOverflowException)
        {
            return ScriptResult.Fail("Skrypt przekroczył limit instrukcji (nieskończona pętla?)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nieoczekiwany błąd silnika skryptowego");
            return ScriptResult.Fail($"Błąd wewnętrzny: {ex.Message}");
        }
    }

    private static Engine BuildEngine(CancellationToken ct)
    {
        return new Engine(options =>
        {
            options.CancellationToken(ct);
            options.TimeoutInterval(TimeSpan.FromSeconds(TimeoutSeconds));
            options.LimitMemory(MaxMemoryBytes);
        });
    }

    private void RegisterApis(Engine engine)
    {
        // Zarejestruj fasadę jako podzielone, czytelne obiekty JS
        engine.SetValue("player", new PlayerApi(_facade));
        engine.SetValue("mic",    new MicApi(_facade));
        engine.SetValue("cart",   new CartApi(_facade));
        engine.SetValue("aux",    new AuxApi(_facade));
        engine.SetValue("rdm",    new RdmApi(_facade));
    }

    // ── Wewnętrzne adaptery JS API ─────────────────────────────────────────────

    public sealed class PlayerApi(IScriptingFacade f)
    {
        public void play()  => f.PlayerPlay();
        public void stop()  => f.PlayerStop();
        public void next()  => f.PlayerNext();
        public void pause() => f.PlayerPause();
    }

    public sealed class MicApi(IScriptingFacade f)
    {
        public void on()     => f.MicOn();
        public void off()    => f.MicOff();
        public void toggle() => f.MicToggle();
    }

    public sealed class CartApi(IScriptingFacade f)
    {
        public void selectTab(int index)    => f.CartSelectTab(index);
        public void triggerSlot(int index)  => f.CartTriggerSlot(index);
    }

    public sealed class AuxApi(IScriptingFacade f)
    {
        public void load(int index, string filePath)       => f.AuxLoad(index, filePath);
        public void play(int index)                        => f.AuxPlay(index);
        public void stop(int index, int fadeMs = 0)         => f.AuxStop(index, fadeMs);
        public void setLoop(int index, bool enabled)       => f.AuxSetLoop(index, enabled);
        public void setVolume(int index, double gain)      => f.AuxSetVolume(index, (float)gain);
    }

    public sealed class RdmApi(IScriptingFacade f)
    {
        public void log(string message)                 => f.Log(message);
        public void delay(int ms)                       => f.Delay(ms);
        public void sendHttp(string url)                => f.SendHttp(url);
        public void sendSerial(string deviceId, string command) => f.SendSerial(deviceId, command);
    }
}
