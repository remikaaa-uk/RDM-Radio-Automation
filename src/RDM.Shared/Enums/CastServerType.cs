namespace RDM.Shared.Enums;

/// <summary>
/// The cast server a profile connects to. The distinction is not cosmetic — BASSenc addresses each
/// one with a different server string:
/// <list type="bullet">
///   <item>SHOUTcast 1 — <c>address:port</c>, password only, no mount point</item>
///   <item>SHOUTcast 2 — <c>address:port,streamid</c>, and it accepts a username</item>
///   <item>Icecast — <c>address:port/mount</c>, and it accepts a username</item>
/// </list>
/// Icecast carries no version entry on purpose: BASSenc does not distinguish 2.3 from 2.4, only
/// the source method (SOURCE or PUT), which is a separate per-profile setting.
/// Persisted as a string, so member order carries no meaning.
/// </summary>
public enum CastServerType { Shoutcast, ShoutcastV2, Icecast }
