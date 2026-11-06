using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCL.Core.Minecraft.Launcher;

/// <summary>Experimental JAR lookup acceleration is strictly opt-in, never a default JVM tuning.</summary>
public static class StartupAccelerationPolicy
{
    public static bool ShouldInject(int javaMajor, bool is64Bit, string arguments, bool hasForge)
    {
        if (javaMajor is not (17 or 21) || !is64Bit || string.IsNullOrEmpty(arguments)) return false;
        if (javaMajor == 17 && !hasForge) return false;
        if (arguments.Count(ch => ch == '"') % 2 != 0 || arguments.Contains("\\\"", StringComparison.Ordinal)) return false;
        var enabled = false;
        var disabled = false;
        foreach (Match match in Regex.Matches(arguments, """(?:[^\s"]|"[^"]*")+"""))
        {
            var token = match.Value.Replace("\"", "", StringComparison.Ordinal);
            // Do not guess options hidden in argument files, or inject the same agent twice.
            if (token.StartsWith('@') || token.StartsWith("-XX:Flags=", StringComparison.Ordinal) ||
                (token.StartsWith("-javaagent:", StringComparison.Ordinal) &&
                 (token.Contains("pcl-moon-startup-agent", StringComparison.OrdinalIgnoreCase) ||
                  token.Contains("mooncl-startup-agent", StringComparison.OrdinalIgnoreCase)))) return false;
            foreach (var prefix in new[] { "-Dpclmoon.startup.enabled=", "-Dmooncl.startup.enabled=" })
                if (token.StartsWith(prefix, StringComparison.Ordinal))
                    enabled = token[prefix.Length..].Equals("true", StringComparison.OrdinalIgnoreCase);
            foreach (var prefix in new[] { "-Dpclmoon.startup.disabled=", "-Dmooncl.startup.disabled=" })
                if (token.StartsWith(prefix, StringComparison.Ordinal))
                    disabled = token[prefix.Length..].Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return enabled && !disabled;
    }
}
