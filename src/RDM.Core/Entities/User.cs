using RDM.Shared.Enums;

namespace RDM.Core.Entities;

public sealed class User
{
    public string UserId { get; init; } = string.Empty;
    public string StudioId { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string PasswordHash { get; init; } = string.Empty;
    public UserRole Role { get; init; }
    public bool Enabled { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
}
