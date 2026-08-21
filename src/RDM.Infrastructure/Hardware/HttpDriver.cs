using Microsoft.Extensions.Logging;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;
using System.Text;

namespace RDM.Infrastructure.Hardware;

/// <summary>
/// Sterownik HTTP do integracji z zewnętrznymi systemami (IoT, enkodery streamów, automatyka).
/// Rejestruje obsługę AutomationSendHttp.
///
/// Format TargetParameter w TriggerActionMapping:
///   "https://host/endpoint"              → GET
///   "POST|https://host/endpoint"         → POST bez body
///   "POST|https://host/endpoint|body"    → POST z body (JSON lub tekst)
/// </summary>
public sealed class HttpDriver
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly ILogger<HttpDriver> _logger;

    public HttpDriver(IActionRegistry actionRegistry, ILogger<HttpDriver> logger)
    {
        _logger = logger;
        actionRegistry.RegisterAction(ActionId.AutomationSendHttp, OnSendHttp);
    }

    private async Task OnSendHttp(IHardwarePayload payload)
    {
        var parameter = (payload is ParameterizedPayload p) ? p.Parameter : null;

        if (string.IsNullOrWhiteSpace(parameter))
        {
            _logger.LogWarning("HttpDriver: brak konfiguracji URL w TargetParameter");
            return;
        }

        var (method, url, body) = ParseParameter(parameter);

        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("HttpDriver: nieprawidłowy URL '{Parameter}'", parameter);
            return;
        }

        try
        {
            HttpResponseMessage response;

            if (method == "POST" || method == "PUT" || method == "PATCH")
            {
                var content = string.IsNullOrEmpty(body)
                    ? new StringContent(string.Empty)
                    : new StringContent(body, Encoding.UTF8, "application/json");

                response = await _httpClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), url)
                {
                    Content = content
                });
            }
            else
            {
                response = await _httpClient.GetAsync(url);
            }

            _logger.LogDebug("HttpDriver: {Method} {Url} → {StatusCode}", method, url, (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("HttpDriver: żądanie {Method} {Url} zwróciło błąd {StatusCode}", method, url, (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HttpDriver: błąd sieci podczas wysyłania {Method} {Url}", method, url);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("HttpDriver: timeout żądania {Method} {Url}", method, url);
        }
    }

    private static (string Method, string Url, string? Body) ParseParameter(string parameter)
    {
        var parts = parameter.Split('|', 3);

        return parts.Length switch
        {
            1 => ("GET", parts[0].Trim(), null),
            2 => (parts[0].Trim().ToUpperInvariant(), parts[1].Trim(), null),
            _ => (parts[0].Trim().ToUpperInvariant(), parts[1].Trim(), parts[2].Trim())
        };
    }
}
