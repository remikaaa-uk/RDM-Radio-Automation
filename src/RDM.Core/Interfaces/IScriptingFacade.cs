namespace RDM.Core.Interfaces;

/// <summary>
/// Fasada skryptowa — jedyne API wystawiane skryptom JS/Lua.
/// Celowo wąskie: tylko operacje bezpieczne dla emisji on-air.
/// </summary>
public interface IScriptingFacade
{
    // ── Odtwarzacz ────────────────────────────────────────────────────────────
    void PlayerPlay();
    void PlayerStop();
    void PlayerNext();
    void PlayerPause();

    // ── Mikrofon ──────────────────────────────────────────────────────────────
    void MicOn();
    void MicOff();
    void MicToggle();

    // ── Cartwall ──────────────────────────────────────────────────────────────
    void CartSelectTab(int tabIndex);
    void CartTriggerSlot(int slotIndex);

    // ── AUX players ───────────────────────────────────────────────────────────
    void AuxLoad(int index, string filePath);
    void AuxPlay(int index);
    void AuxStop(int index, int fadeMs = 0);
    void AuxSetLoop(int index, bool enabled);
    void AuxSetVolume(int index, float gain);   // 0.0 – 1.0

    // ── Integracje zewnętrzne ─────────────────────────────────────────────────
    void SendHttp(string url);                        // GET; "POST|url|body" też OK
    void SendSerial(string deviceId, string command);

    // ── Narzędzia skryptowe ───────────────────────────────────────────────────
    void Log(string message);
    void Delay(int milliseconds);                     // max 4000ms; blokujące w wątku skryptu
}
