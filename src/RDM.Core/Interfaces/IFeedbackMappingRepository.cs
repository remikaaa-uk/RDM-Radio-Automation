using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IFeedbackMappingRepository
{
    Task<IReadOnlyList<FeedbackRule>> GetAllAsync();
    Task SaveAsync(FeedbackRule rule);
    Task DeleteAsync(Guid id);
}
