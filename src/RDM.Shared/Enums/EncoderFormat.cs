namespace RDM.Shared.Enums;

/// <summary>
/// Formats BASSenc can produce on Windows through a distributable add-on DLL.
/// AAC is deliberately absent: bassenc_aac.dll exists only on Apple platforms, where the OS
/// supplies the encoder; on Windows it would need an external process fed through STDIN.
/// OPUS covers the same need — Icecast serves it natively and it is patent-free.
/// Persisted as a string ("MP3"/"OGG"/"OPUS"), so the member order carries no meaning.
/// </summary>
public enum EncoderFormat { Mp3, Ogg, Opus }
