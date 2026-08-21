using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;

namespace RDM.Core.Services;

/// <summary>
/// Tells the live streaming sessions what is playing.
///
/// Sits alongside <see cref="StreamTitlesService"/> rather than inside it: that one answers "write
/// the current track to a file for an external encoder", this one answers "tell our own cast server
/// what is on". They share the composition — <see cref="StreamTitlesService.ComposeTitleAsync"/> —
/// so a station has one definition of what a title looks like, including the category filter that
/// keeps jingle and advert names off the stream.
///
/// It deliberately does not read profiles. Each session already holds its own, so it decides for
/// itself whether to forward this line, substitute a fixed one, or send nothing. That keeps this
/// class free of the repository — and free of the scoped-versus-singleton problem that injecting
/// one into a long-lived subscriber would create in the API host.
/// </summary>
public sealed class EncoderTitleService : IDisposable
{
    private readonly IEventBus                       _eventBus;
    private readonly IAudioEngine                    _audioEngine;
    private readonly StreamTitlesService             _titles;
    private readonly ILogger<EncoderTitleService>    _log;
    private readonly Action<PlaylistItemStartedEvent> _handler;
    private readonly CancellationTokenSource         _cts = new();

    public EncoderTitleService(
        IEventBus                    eventBus,
        IAudioEngine                 audioEngine,
        StreamTitlesService          titles,
        ILogger<EncoderTitleService> log)
    {
        _eventBus    = eventBus;
        _audioEngine = audioEngine;
        _titles      = titles;
        _log         = log;

        _handler = evt => _ = SafeHandleAsync(evt);
        _eventBus.Subscribe(_handler);
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe(_handler);
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>
    /// A title is cosmetic; the audio is not. Nothing here — a database lookup for $year$, a server
    /// that rejects metadata — may be allowed to surface on the playout path, so every failure ends
    /// in the log and nowhere else.
    /// </summary>
    private async Task SafeHandleAsync(PlaylistItemStartedEvent evt)
    {
        try
        {
            var title = await _titles.ComposeTitleAsync(evt, _cts.Token);

            // Null means the category filter excluded this track — a jingle or an advert. The
            // sessions keep whatever title they last had, which is the right thing to leave up.
            if (string.IsNullOrWhiteSpace(title)) return;

            await _audioEngine.SetEncoderTitleAsync(title, _cts.Token);
            _log.LogDebug("Encoder titles: {Title}", title);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Encoder titles: could not update the stream title");
        }
    }
}
