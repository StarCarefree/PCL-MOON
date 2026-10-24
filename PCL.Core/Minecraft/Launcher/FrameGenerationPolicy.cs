using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>Opt-in presentation hook; GPU/backend support is checked inside the game, not inferred from a vendor name.</summary>
public static class FrameGenerationPolicy
{
    public static bool ShouldInject(bool generation, bool diagnostics, int javaMajor, bool is64Bit, string arguments, bool settingsButton = false, bool independentPacing = false, bool superResolution = false)
    {
        if ((!generation && !diagnostics && !settingsButton && !independentPacing && !superResolution) || javaMajor < 8 || !is64Bit) return false;
        // Do not guess an ambiguous command line or duplicate a manually supplied agent.
        if (arguments.Count(ch => ch == '"') % 2 != 0 || arguments.Contains("\\\"", StringComparison.Ordinal)) return false;
        var tokens = Regex.Matches(arguments, """(?:[^\s"]|"[^"]*")+""")
            .Select(match => match.Value.Trim('"')).ToArray();
        if (tokens.Any(token => token.Equals("-Dpclmoon.framegen.disabled=true", StringComparison.OrdinalIgnoreCase) ||
                                (token.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase) &&
                                 token.Contains("framegen", StringComparison.OrdinalIgnoreCase)))) return false;
        return true;
    }

    /// <summary>Use the instance directory, not javaw.exe or a possibly shared game working directory.</summary>
    public static string ProfileKey(string instancePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instancePath)).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
