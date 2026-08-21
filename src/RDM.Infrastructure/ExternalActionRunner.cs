using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure;

/// <summary>
/// Default <see cref="IExternalActionRunner"/>: launches external files and makes HTTP calls
/// for scheduled-event actions. Gated by <c>scheduler:allow_external_execution</c> in rdm.config.json
/// (default: false) so process launches never happen unless the operator opts in.
/// </summary>
public sealed class ExternalActionRunner : IExternalActionRunner
{
    // One HttpClient reused across calls (recommended pattern). Per-call timeout is applied
    // via a linked CancellationTokenSource, not HttpClient.Timeout, so the shared instance
    // stays immutable.
    private static readonly HttpClient Http = new();

    private readonly IConfiguration                 _config;
    private readonly ILogger<ExternalActionRunner>  _logger;

    public ExternalActionRunner(IConfiguration config, ILogger<ExternalActionRunner> logger)
    {
        _config = config;
        _logger = logger;
    }

    private bool ExternalAllowed =>
        bool.TryParse(_config["scheduler:allow_external_execution"], out var allowed) && allowed;

    // ── File / script / executable ─────────────────────────────────────────────

    public async Task<ExternalRunResult> RunFileAsync(
        string path, string? arguments, string? workingDirectory,
        int timeoutMs, bool captureOutput, CancellationToken ct = default)
    {
        if (!ExternalAllowed)
            throw new ExternalActionDisabledException();

        if (string.IsNullOrWhiteSpace(path))
            return new ExternalRunResult(false, -1, null, "Empty path.");

        if (timeoutMs <= 0)
            timeoutMs = 30_000;

        var psi = new ProcessStartInfo
        {
            FileName         = path,
            Arguments        = arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                                   ? Path.GetDirectoryName(path) ?? string.Empty
                                   : workingDirectory,
            // UseShellExecute=true lets the OS resolve how to run any file type (.py/.ps1/.bat/…),
            // but forbids output redirection. Capturing output requires a directly launchable
            // executable, so we flip the flag when the operator asks for output.
            UseShellExecute        = !captureOutput,
            CreateNoWindow         = captureOutput,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError  = captureOutput,
        };

        _logger.LogInformation(
            "EXECUTE_FILE: '{Path}' args='{Args}' timeout={Timeout}ms capture={Capture}",
            path, arguments, timeoutMs, captureOutput);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EXECUTE_FILE: failed to start '{Path}'", path);
            return new ExternalRunResult(false, -1, null, ex.Message);
        }

        if (process is null)
            return new ExternalRunResult(false, -1, null, "Process could not be started.");

        using (process)
        {
            // Start reading before waiting so a chatty child cannot deadlock on a full pipe buffer.
            Task<string>? stdout = captureOutput ? process.StandardOutput.ReadToEndAsync(ct) : null;
            Task<string>? stderr = captureOutput ? process.StandardError.ReadToEndAsync(ct)  : null;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process, path);
                return new ExternalRunResult(false, -1, null, $"Timed out after {timeoutMs} ms.");
            }

            string? output = stdout is not null ? await stdout : null;
            string? error  = stderr is not null ? await stderr : null;

            bool success = process.ExitCode == 0;
            if (!success)
                _logger.LogWarning("EXECUTE_FILE '{Path}' exited with code {Code}", path, process.ExitCode);

            return new ExternalRunResult(success, process.ExitCode, output, error);
        }
    }

    private void TryKill(Process process, string path)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            _logger.LogWarning("EXECUTE_FILE '{Path}' killed after timeout", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EXECUTE_FILE '{Path}' — kill after timeout failed", path);
        }
    }

    // ── HTTP ───────────────────────────────────────────────────────────────────

    public async Task<ExternalRunResult> RunHttpAsync(
        string method, string url, string? body,
        IReadOnlyDictionary<string, string>? headers, int timeoutMs, CancellationToken ct = default)
    {
        if (!ExternalAllowed)
            throw new ExternalActionDisabledException();

        if (string.IsNullOrWhiteSpace(url))
            return new ExternalRunResult(false, -1, null, "Empty URL.");

        if (timeoutMs <= 0)
            timeoutMs = 30_000;

        using var request = new HttpRequestMessage(
            new HttpMethod(string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant()),
            url);

        if (!string.IsNullOrEmpty(body))
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (headers is not null)
            foreach (var (key, value) in headers)
                request.Headers.TryAddWithoutValidation(key, value);

        _logger.LogInformation("HTTP_CALL: {Method} {Url} timeout={Timeout}ms", request.Method, url, timeoutMs);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            using var response = await Http.SendAsync(request, timeoutCts.Token);
            string content = await response.Content.ReadAsStringAsync(ct);

            bool success = response.IsSuccessStatusCode;
            if (!success)
                _logger.LogWarning("HTTP_CALL {Url} returned {Status}", url, (int)response.StatusCode);

            return new ExternalRunResult(success, (int)response.StatusCode, content, success ? null : content);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ExternalRunResult(false, -1, null, $"Timed out after {timeoutMs} ms.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP_CALL {Url} failed", url);
            return new ExternalRunResult(false, -1, null, ex.Message);
        }
    }
}
