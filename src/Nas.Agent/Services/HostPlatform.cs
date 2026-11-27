using System.Runtime.InteropServices;

namespace Nas.Agent.Services;

/// <summary>
/// Processor architecture of the NAS. Only x86-64 is officially supported: releases are published for it alone. A build made
/// from the sources for another architecture (ARM64) runs, but some features are switched off.
/// </summary>
public static class HostPlatform
{
    /// <summary>True on x86-64. The <c>Platform:Architecture</c> setting overrides the detected value (tests).</summary>
    public static bool IsOfficial(IConfiguration cfg)
        => string.Equals(cfg["Platform:Architecture"] ?? RuntimeInformation.OSArchitecture.ToString(), nameof(Architecture.X64), StringComparison.OrdinalIgnoreCase);
}
