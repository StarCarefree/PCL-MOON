using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>Opt-in presentation hook; GPU/backend support is checked inside the game, not inferred from a vendor name.</summary>
public static class FrameGenerationPolicy
{
    public static bool ShouldInject(bool generation, bool diagnostics, int javaMajor, bool is64Bit, string arguments)
    {
        if ((!generation && !diagnostics) || javaMajor < 8 || !is64Bit) return false;
        // Do not guess an ambiguous command line or duplicate a manually supplied agent.
        if (arguments.Count(ch => ch == '"') % 2 != 0 || arguments.Contains("\\\"", StringComparison.Ordinal)) return false;
        var tokens = Regex.Matches(arguments, """(?:[^\s"]|"[^"]*")+""")
            .Select(match => match.Value.Trim('"')).ToArray();
        if (tokens.Any(token => token.Equals("-Dpclmoon.framegen.disabled=true", StringComparison.OrdinalIgnoreCase) ||
                                (token.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase) &&
                                 token.Contains("framegen", StringComparison.OrdinalIgnoreCase)))) return false;
        return true;
    }
}
