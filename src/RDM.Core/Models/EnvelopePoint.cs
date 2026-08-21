namespace RDM.Core.Models;

/// One control point on a volume envelope. TimeS is seconds from the audible
/// start of the track (after TrimStart); Volume is linear gain [0.0 .. 1.0].
public sealed record EnvelopePoint(double TimeS, double Volume);
