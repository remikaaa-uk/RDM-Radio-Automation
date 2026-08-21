namespace RDM.Core.Interfaces;

/// <summary>
/// Runs actions that reach outside RDM: launching an external file/script/executable
/// and making an outbound HTTP request. Used by scheduled-event actions
/// EXECUTE_FILE and HTTP_CALL.
///
/// Implementations MUST honour a configuration gate (scheduler:allow_external_execution).
/// When the gate is disabled every call throws <see cref="ExternalActionDisabledException"/>
/// so the operator is never surprised by silent process launches.
/// </summary>
public interface IExternalActionRunner
{
    /// <summary>
    /// Launches <paramref name="path"/> (any file the OS knows how to start —
    /// .ps1, .py, .sh, .bat, .js, .exe, …) and waits for it to exit or the timeout to elapse.
    /// </summary>
    Task<ExternalRunResult> RunFileAsync(
        string  path,
        string? arguments,
        string? workingDirectory,
        int     timeoutMs,
        bool    captureOutput,
        CancellationToken ct = default);

    /// <summary>Performs an outbound HTTP request.</summary>
    Task<ExternalRunResult> RunHttpAsync(
        string                        method,
        string                        url,
        string?                       body,
        IReadOnlyDictionary<string, string>? headers,
        int                           timeoutMs,
        CancellationToken ct = default);
}

/// <summary>Outcome of an external action. ExitCode doubles as the HTTP status code for HTTP calls.</summary>
public sealed record ExternalRunResult(
    bool    Success,
    int     ExitCode,
    string? Output,
    string? Error);

/// <summary>Thrown when external execution is requested but disabled in configuration.</summary>
public sealed class ExternalActionDisabledException : Exception
{
    public ExternalActionDisabledException()
        : base("External execution is disabled. Enable scheduler:allow_external_execution in rdm.config.json to permit it.") { }
}
