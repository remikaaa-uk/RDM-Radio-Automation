using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Infrastructure.Database;
using RDM.Shared.Enums;

namespace RDM.API.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    public Mock<IDbConnectionFactory> DbConnectionFactoryMock { get; } = new();
    public Mock<IUserRepository> UserRepositoryMock { get; } = new();
    public Mock<IAssetRepository> AssetRepositoryMock { get; } = new();
    public Mock<IScheduledEventRepository> ScheduledEventRepositoryMock { get; } = new();
    public Mock<IPlaylistRepository> PlaylistRepositoryMock { get; } = new();
    public Mock<IAudioEngine> AudioEngineMock { get; } = new();
    public Mock<IPlaylistController> PlaylistControllerMock { get; } = new();
    public Mock<IAudioSettingsRepository> AudioSettingsRepositoryMock { get; } = new();
    public Mock<IEncoderProfileRepository> EncoderProfileRepositoryMock { get; } = new();
    public Mock<ISecretProtector> SecretProtectorMock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = false;
            options.ValidateOnBuild = false;
        });

        builder.ConfigureTestServices(services =>
        {
            // Replace DatabaseBootstrapper with fake
            services.AddSingleton<DatabaseBootstrapper, FakeDatabaseBootstrapper>();

            // Mock DB Connection Factory
            DbConnectionFactoryMock.Setup(f => f.CreateConnection()).Returns(new FakeDbConnection());
            services.AddSingleton(DbConnectionFactoryMock.Object);

            // Register Mock repositories
            services.AddScoped(_ => UserRepositoryMock.Object);
            services.AddScoped(_ => AssetRepositoryMock.Object);
            services.AddScoped(_ => ScheduledEventRepositoryMock.Object);
            services.AddScoped(_ => PlaylistRepositoryMock.Object);
            services.AddScoped(_ => AudioSettingsRepositoryMock.Object);
            services.AddScoped(_ => EncoderProfileRepositoryMock.Object);
            services.AddSingleton(_ => SecretProtectorMock.Object);

            // Register Mock engines
            services.AddSingleton(_ => AudioEngineMock.Object);
            services.AddSingleton(_ => PlaylistControllerMock.Object);

            // Default mock setups
            AudioEngineMock.Setup(e => e.InitializeAsync(It.IsAny<AudioSettings>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var defaultSettings = new AudioSettings
            {
                SettingsId = "test-settings-id",
                StudioId = "test-studio-id",
                ApiAuthEnabled = true,
                ApiAnonymousLocal = false,
                ApiUsername = "admin",
                ApiPasswordHash = BCrypt.Net.BCrypt.HashPassword("secret", workFactor: 4) // fast hash for testing
            };

            AudioSettingsRepositoryMock.Setup(r => r.GetByStudioAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(defaultSettings);

            // WaveformRescanService calls this on startup — return empty list so it doesn't crash
            AssetRepositoryMock
                .Setup(r => r.GetAllForWaveformScanAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<AssetWaveformInfo>());

            // The real engine answers this from the BASSenc DLLs on disk, which a test host has
            // not loaded. Default to "available" so format checks do not swallow every other
            // assertion; tests that care about a missing add-on override it explicitly.
            AudioEngineMock
                .Setup(e => e.IsEncoderFormatAvailable(It.IsAny<EncoderFormat>()))
                .Returns(true);

            // Round-trip stand-in for DPAPI: readable, reversible, and obviously not real crypto.
            SecretProtectorMock
                .Setup(s => s.Protect(It.IsAny<string?>()))
                .Returns((string? plain) => plain is null ? null : System.Text.Encoding.UTF8.GetBytes(plain));
            SecretProtectorMock
                .Setup(s => s.Unprotect(It.IsAny<byte[]?>()))
                .Returns((byte[]? blob) => blob is null ? null : System.Text.Encoding.UTF8.GetString(blob));

        });
    }
}
