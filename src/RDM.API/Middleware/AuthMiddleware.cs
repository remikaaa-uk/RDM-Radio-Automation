using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RDM.API.Middleware;

public sealed class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthMiddleware> _logger;

    public AuthMiddleware(RequestDelegate next, ILogger<AuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Resolve scoped dependencies from RequestServices
        var authService = context.RequestServices.GetRequiredService<IAuthService>();
        var studioContext = context.RequestServices.GetRequiredService<StudioContext>();
        var settings = context.RequestServices.GetRequiredService<AudioSettings>();

        // 1. Check if auth is disabled globally
        if (!settings.ApiAuthEnabled)
        {
            context.Items["Role"] = UserRole.Admin;
            await _next(context);
            return;
        }

        // 2. Check if local anonymous bypass is enabled and the connection is loopback only.
        //    Deliberately NOT the whole LAN — a private-range bypass would let any host on the
        //    subnet reach the API unauthenticated. Anonymous access is limited to this machine.
        if (settings.ApiAnonymousLocal && IsLoopback(context.Connection.RemoteIpAddress))
        {
            context.Items["Role"] = UserRole.Operator;
            await _next(context);
            return;
        }

        // 3. Perform Basic Authentication
        string? authHeader = context.Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedResponse(context);
            return;
        }

        UserRole? authenticatedRole = null;
        User? authenticatedUser = null;

        try
        {
            string base64Credentials = authHeader.Substring(6).Trim();
            byte[] credentialBytes = Convert.FromBase64String(base64Credentials);
            string credentials = Encoding.UTF8.GetString(credentialBytes);
            int colonIndex = credentials.IndexOf(':');

            if (colonIndex != -1)
            {
                string username = credentials.Substring(0, colonIndex);
                string password = credentials.Substring(colonIndex + 1);

                // A. Fallback validation against Settings
                if (settings.ApiUsername == username && !string.IsNullOrEmpty(settings.ApiPasswordHash))
                {
                    if (BCrypt.Net.BCrypt.Verify(password, settings.ApiPasswordHash))
                    {
                        authenticatedRole = UserRole.Operator;
                    }
                }

                if (authenticatedRole == null)
                {
                    // B. Database validation
                    var user = await authService.AuthenticateAsync(studioContext.StudioId, username, password, context.RequestAborted);
                    if (user != null)
                    {
                        authenticatedUser = user;
                        authenticatedRole = user.Role;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing authorization header.");
        }

        if (authenticatedRole != null)
        {
            if (authenticatedUser != null)
            {
                context.Items["User"] = authenticatedUser;
            }
            context.Items["Role"] = authenticatedRole.Value;
            await _next(context);
            return;
        }

        await WriteUnauthorizedResponse(context);
    }

    private static async Task WriteUnauthorizedResponse(HttpContext context)
    {
        context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        context.Response.ContentType = "application/json";
        
        // Add WWW-Authenticate header to trigger browser auth dialog
        context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"RDM API\"";

        var responseDto = new ErrorResponseDto(
            "UNAUTHORIZED",
            "Valid credentials required.",
            context.TraceIdentifier
        );

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(responseDto, jsonOptions));
    }

    private static bool IsLoopback(IPAddress? address)
    {
        if (address == null) return false;
        if (IPAddress.IsLoopback(address)) return true;

        // Handle IPv4-mapped IPv6 loopback (e.g. ::ffff:127.0.0.1)
        if (address.IsIPv4MappedToIPv6)
        {
            return IPAddress.IsLoopback(address.MapToIPv4());
        }

        return false;
    }
}
