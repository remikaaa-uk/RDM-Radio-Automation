using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RDM.Core;
using RDM.Shared.DTOs;

namespace RDM.API.Middleware;

public sealed class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An unhandled exception occurred during request processing. Path: {Path}", context.Request.Path);
            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/json";

        var (statusCode, errorCode, message) = exception switch
        {
            DuplicateAssetException => (
                HttpStatusCode.Conflict,
                "ASSET_ALREADY_EXISTS",
                exception.Message
            ),
            KeyNotFoundException => (
                HttpStatusCode.NotFound,
                "RESOURCE_NOT_FOUND",
                exception.Message
            ),
            ArgumentException => (
                HttpStatusCode.BadRequest,
                "BAD_REQUEST",
                exception.Message
            ),
            UnauthorizedAccessException => (
                HttpStatusCode.Unauthorized,
                "UNAUTHORIZED",
                exception.Message
            ),
            InvalidOperationException => (
                HttpStatusCode.Conflict,
                "BAD_REQUEST",
                exception.Message
            ),
            _ => (
                HttpStatusCode.InternalServerError,
                "INTERNAL_ERROR",
                "An unexpected internal error occurred."
            )
        };

        context.Response.StatusCode = (int)statusCode;

        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        var responseDto = new ErrorResponseDto(errorCode, message, traceId);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var jsonString = JsonSerializer.Serialize(responseDto, jsonOptions);
        await context.Response.WriteAsync(jsonString);
    }
}
