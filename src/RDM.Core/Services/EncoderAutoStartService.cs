using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Interfaces;

namespace RDM.Core.Services;

/// <summary>
/// Starts the streaming profiles flagged <see cref="EncoderProfile.AutoStart"/> once the audio
/// engine is up.
///
/// Sits between the repository and the engine rather than inside either. The engine deliberately
/// knows nothing about stored profiles — it is handed one and told to stream it — and the
/// repository knows nothing about audio. This is the only place that needs both, plus the secret
/// protector to turn a stored blob back into a password.
///
/// <b>Nothing here may throw.</b> This runs on the startup path: a profile pointing at a dead
/// host, a format whose add-on was never installed, or a password blob copied from another machine
/// must each cost that one profile and nothing else. A studio that cannot open its playout window
/// because one backup stream is misconfigured is worse off than one that simply is not streaming.
/// </summary>
public sealed class EncoderAutoStartService
{
    private readonly IEncoderProfileRepository _profiles;
    private readonly IAudioEngine              _audioEngine;
    private readonly ISecretProtector          _secrets;
    private readonly StudioContext             _studioContext;
    private readonly ILogger<EncoderAutoStartService> _log;

    public EncoderAutoStartService(
        IEncoderProfileRepository profiles,
        IAudioEngine              audioEngine,
        ISecretProtector          secrets,
        StudioContext             studioContext,
        ILogger<EncoderAutoStartService> log)
    {
        _profiles      = profiles;
        _audioEngine   = audioEngine;
        _secrets       = secrets;
        _studioContext = studioContext;
        _log           = log;
    }

    /// <summary>
    /// Starts every auto-start profile that can be started. Returns how many were actually handed
    /// to the engine — the rest are logged with the reason they were skipped.
    /// </summary>
    public async Task<int> StartAllAsync(CancellationToken ct = default)
    {
        // No engine, no streaming. The application runs in no-audio mode after a failed init, and
        // StartEncoderAsync would throw — which on this path would take the startup with it.
        if (!_audioEngine.IsInitialized)
        {
            _log.LogInformation("Encoder auto-start skipped — the audio engine is not running.");
            return 0;
        }

        IReadOnlyList<EncoderProfile> profiles;
        try
        {
            profiles = await _profiles.GetAutoStartAsync(_studioContext.StudioId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Encoder auto-start skipped — could not read the profiles.");
            return 0;
        }

        if (profiles.Count == 0) return 0;

        var started = 0;
        foreach (var profile in profiles)
        {
            if (ct.IsCancellationRequested) break;
            if (await TryStartAsync(profile, ct)) started++;
        }

        _log.LogInformation("Encoder auto-start: {Started} of {Total} profile(s) started.",
            started, profiles.Count);
        return started;
    }

    private async Task<bool> TryStartAsync(EncoderProfile profile, CancellationToken ct)
    {
        if (!_audioEngine.IsEncoderFormatAvailable(profile.Format))
        {
            _log.LogWarning(
                "Encoder auto-start: '{Name}' skipped — {Format} is not available in this installation.",
                profile.Name, profile.Format);
            return false;
        }

        string? password;
        try
        {
            password = _secrets.Unprotect(profile.PasswordEncrypted);
        }
        catch (Exception ex)
        {
            // DPAPI blobs are machine-bound. A profile restored from a backup taken elsewhere
            // decrypts to nothing, and starting it anyway would only produce a rejected login.
            _log.LogWarning(ex,
                "Encoder auto-start: '{Name}' skipped — its password cannot be decrypted on this machine.",
                profile.Name);
            return false;
        }

        try
        {
            await _audioEngine.StartEncoderAsync(profile, password, ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Encoder auto-start: '{Name}' could not be started.", profile.Name);
            return false;
        }
    }
}
