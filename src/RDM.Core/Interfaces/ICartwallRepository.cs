using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface ICartwallRepository
{
    Task<Cartwall?> GetByIdAsync(string cartwallId, CancellationToken ct = default);
    Task<IReadOnlyList<Cartwall>> GetByStudioAsync(string studioId, CancellationToken ct = default);
    Task<IReadOnlyList<CartSlot>> GetSlotsAsync(string cartwallId, CancellationToken ct = default);
    Task<CartSlot?> GetSlotByIdAsync(string slotId, CancellationToken ct = default);
    Task CreateAsync(Cartwall cartwall, CancellationToken ct = default);
    Task UpdateAsync(Cartwall cartwall, CancellationToken ct = default);
    Task DeleteAsync(string cartwallId, CancellationToken ct = default);
    Task UpsertSlotAsync(CartSlot slot, CancellationToken ct = default);

    /// Ensures that empty slot rows 1..<paramref name="count"/> exist for the given cartwall.
    /// Slots beyond <paramref name="count"/> are left untouched in the DB.
    Task EnsureSlotsAsync(string cartwallId, byte count, CancellationToken ct = default);
}
