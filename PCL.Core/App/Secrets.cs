using PCL.Core.Utils.Exts;
using PCL.Core.Utils.OS;

namespace PCL.Core.App;

// ReSharper disable InconsistentNaming
public static class Secrets
{
    /// <summary>
    /// 微软 OAuth 的 Client ID
    /// </summary>
    public static string MSOAuthClientId { get; } = FirstSecret("MS_CLIENT_ID", "MICROSOFT_CLIENT_ID", "");

    /// <summary>
    /// 微软 OAuth 的 Client Secret。机密客户端换 token 时使用。
    /// </summary>
    public static string MSOAuthClientSecret { get; } = FirstSecret("MS_CLIENT_SECRET", "MICROSOFT_CLIENT_SECRET", "");

    /// <summary>
    /// CurseForge API 的 Client ID
    /// </summary>
    public static string CurseForgeAPIKey { get; } = EnvironmentInterop.GetSecret("CURSEFORGE_API_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 遥测密钥
    /// </summary>
    public static string TelemetryKey { get; } = EnvironmentInterop.GetSecret("TELEMETRY_KEY", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    /// <summary>
    /// 当前版本的 Git 提交 SHA
    /// </summary>
    public static string CommitHash { get; } = EnvironmentInterop.GetSecret("GITHUB_SHA", readEnvDebugOnly: true).ReplaceNullOrEmpty();

    private static string FirstSecret(string primary, string alias, string builtIn)
    {
        var value = EnvironmentInterop.GetSecret(primary, readEnvDebugOnly: true);
        if (string.IsNullOrWhiteSpace(value))
            value = EnvironmentInterop.GetSecret(alias, readEnvDebugOnly: true);
        if (string.IsNullOrWhiteSpace(value))
            value = builtIn;
        return value.ReplaceNullOrEmpty();
    }
}
