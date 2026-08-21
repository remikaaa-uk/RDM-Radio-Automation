namespace RDM.Audio;

public sealed class AudioEngineException : Exception
{
    public AudioEngineException(string message) : base(message) { }
    public AudioEngineException(string message, Exception inner) : base(message, inner) { }
}
