using Microsoft.Extensions.Logging;
using RDM.Shared.Enums;

namespace RDM.Audio.Engine.Output;

/// <summary>
/// Selects the output backend for the configured driver mode. Only DirectSound is
/// implemented today; every other mode falls back to DirectSound with a clear warning
/// so the app keeps running while the mode is honestly reported (via
/// <see cref="IOutputBackend.EffectiveMode"/> and the log) as not-yet-available.
/// </summary>
internal static class OutputBackendFactory
{
    public static IOutputBackend Create(DriverType mode, ILogger log)
    {
        switch (mode)
        {
            case DriverType.DirectSound:
                return new DirectSoundBackend(mode, log);

            case DriverType.WasapiShared:
            case DriverType.WasapiExclusive:
                return new WasapiBackend(mode, log);

            case DriverType.Asio:
                return new AsioBackend(mode, log);

            default:
                log.LogWarning("OutputMode {Mode} nieznany — używam DirectSound.", mode);
                return new DirectSoundBackend(mode, log);
        }
    }
}
