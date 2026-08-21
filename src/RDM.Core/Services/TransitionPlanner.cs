namespace RDM.Core.Services;

/// <summary>One upcoming queue item, reduced to what the planner needs.</summary>
public readonly record struct PlannerItem(
    bool  IsDummy,
    bool  IsSweeper,
    uint  EffectiveDurationMs,
    int?  LeadInMs,
    uint? CrossfadeMs);

/// <summary>A sweeper to float as an overlay, identified by its 1-based offset from the
/// current item, with the trigger position (ms) on the current track's stream.</summary>
public readonly record struct OverlaySchedule(int Offset, long TriggerAtMs);

/// <summary>Result of <see cref="TransitionPlanner.Plan"/>.</summary>
public readonly record struct TransitionPlan(
    int MainTrackOffset,                       // 1-based offset to the next sequential track; -1 if none
    uint CrossfadeMs,                          // overlap (ms) of the current track with that track
    IReadOnlyList<OverlaySchedule> Overlays);  // sweepers to float over the transition

/// <summary>
/// Plans how the current track transitions into the next *sequential* track when zero or more
/// short sweepers sit between them in the queue (uwagi.md §1 — LeadInMs follow-up).
///
/// Positions follow the segue-editor's chain layout:
///   start[i] = start[i-1] + effDur[i-1] − overlap[i],  overlap = LeadInMs ?? CrossfadeMs ?? default
///
/// A sweeper whose computed start lands before the current track ends becomes a floating OVERLAY
/// (scheduled on the current stream) and is skipped in the sequential chain — the current track
/// then crossfades directly into the following track with the deep overlap the editor shows.
/// A sweeper that does not overlap the current track stays a normal sequential item.
/// Pure / no IO, so it is unit-testable in isolation.
/// </summary>
public static class TransitionPlanner
{
    public static TransitionPlan Plan(
        uint currentEffectiveEndMs,
        IReadOnlyList<PlannerItem> upcoming,
        uint defaultCrossfadeMs)
    {
        var overlays = new List<OverlaySchedule>();

        double prevStart  = 0;                       // the current track starts the local timeline at 0
        double prevEffDur = currentEffectiveEndMs;

        for (int k = 0; k < upcoming.Count; k++)
        {
            var it = upcoming[k];
            if (it.IsDummy) continue;                // transparent: skipped, contributes no time

            long   overlap = it.LeadInMs ?? (long)(it.CrossfadeMs ?? defaultCrossfadeMs);
            double start   = prevStart + prevEffDur - overlap;

            if (it.IsSweeper && start < currentEffectiveEndMs)
            {
                // Floats over the current track's tail → schedule as an overlay and walk past it.
                long trig = (long)Math.Clamp(start, 0, currentEffectiveEndMs);
                overlays.Add(new OverlaySchedule(k + 1, trig));
                prevStart  = start;
                prevEffDur = it.EffectiveDurationMs;
                continue;
            }

            // First track that plays sequentially (a normal track, or a non-overlapping sweeper).
            uint crossfade = (uint)Math.Clamp(currentEffectiveEndMs - start, 0, currentEffectiveEndMs);
            return new TransitionPlan(k + 1, crossfade, overlays);
        }

        return new TransitionPlan(-1, 0, overlays);
    }
}
