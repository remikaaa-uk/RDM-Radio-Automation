using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IFeedbackMappingCache
{
    Task InitializeAsync();
    Task ReloadAsync();
    IReadOnlyList<FeedbackRule> GetFeedbackRules(string eventName);
}
