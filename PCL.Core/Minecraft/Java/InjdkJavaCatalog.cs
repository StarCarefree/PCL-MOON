using System;
using System.Linq;

namespace PCL.Core.Minecraft.Java;

public static class InjdkJavaCatalog
{
    // 固定已核实的镜像文件及 Azul 官方 SHA-256，回退不依赖官方接口可用性。
    private static readonly (Version Version, bool Is64Bit, JavaDownloadPackage Package)[] Packages =
    [
        Entry("zulu8.94.0.17-ca-jdk8.0.492-win_i686.zip", "8.0.492", false,
            "756a277bff678b1aef33a883c91b298493e10f6d7fdd582021c6e4238e2da43c"),
        Entry("zulu8.94.0.17-ca-jdk8.0.492-win_x64.zip", "8.0.492", true,
            "376e0e5e76876a7ef916600aa0c32010db3b6a3238520d3ae187a8a19e771b59"),
        Entry("zulu11.88.17-ca-jdk11.0.31-win_i686.zip", "11.0.31", false,
            "44df545e39140b3e822207830a5bad6e6447914765cd44592838b66b6e1a9162"),
        Entry("zulu11.88.17-ca-jdk11.0.31-win_x64.zip", "11.0.31", true,
            "76c6592dc7ae74db7b8bf4612a13a000209277c1da670dc1d0bcc1d3460e4425"),
        Entry("zulu17.66.19-ca-jdk17.0.19-win_i686.zip", "17.0.19", false,
            "c3be74f9c3c402d6a8fd33f11dbe436378814b836feb91a347441d964d0fe706"),
        Entry("zulu17.66.19-ca-jdk17.0.19-win_x64.zip", "17.0.19", true,
            "463c85454c45fd3827df7ccfaa2b14f136eb972387da697a349a42867334328a"),
        Entry("zulu21.50.19-ca-jdk21.0.11-win_x64.zip", "21.0.11", true,
            "84c73a4dcf8400ac01c1fe2d622cc319199e93c1856c200cb50c1bc16dc7dc97"),
        Entry("zulu25.34.17-ca-jdk25.0.3-win_x64.zip", "25.0.3", true,
            "ddd68ee54e78b6b19388f517b8a61fbed2856ae3320febe42449bac3021e2729"),
        Entry("zulu26.30.11-ca-jdk26.0.1-win_x64.zip", "26.0.1", true,
            "8f76f408b0e228974b0c9578a92759189bb5dcfa7f7a521582777e404a3244a0")
    ];

    public static JavaDownloadPackage? SelectPackage(JavaDownloadRequest request) =>
        Packages.Where(item => item.Is64Bit == request.Is64Bit && request.Accepts(item.Version))
            .OrderByDescending(item => item.Version)
            .Select(item => item.Package)
            .FirstOrDefault();

    private static (Version, bool, JavaDownloadPackage) Entry(string name, string version, bool is64Bit, string sha256)
    {
        var parsedVersion = Version.Parse(version);
        return (parsedVersion, is64Bit, new JavaDownloadPackage("Zulu", name,
            $"https://d10.injdk.cn/openjdk/zulu/{parsedVersion.Major}/{name}", sha256, -1));
    }
}
