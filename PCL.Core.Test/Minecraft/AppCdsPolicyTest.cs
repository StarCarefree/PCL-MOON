using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class AppCdsPolicyTest
{
    private const string ValidArchive = @"D:\PCL\appcds\0123456789abcdef01234567.jsa";

    [TestMethod]
    [DataRow(true, 21, true, true, ValidArchive, "-Xmx4G", true)]
    [DataRow(false, 21, true, true, ValidArchive, "-Xmx4G", false)]
    [DataRow(true, 17, true, true, ValidArchive, "-Xmx4G", false)]
    [DataRow(true, 19, true, true, ValidArchive, "-Xmx4G", true)]
    [DataRow(true, 25, true, true, ValidArchive, "-Xmx4G", true)]
    [DataRow(true, 26, true, true, ValidArchive, "-Xmx4G", false)]
    [DataRow(true, 21, false, true, ValidArchive, "-Xmx4G", false)]
    [DataRow(true, 21, true, false, ValidArchive, "-Xmx4G", false)]
    [DataRow(true, 21, true, true, @"D:\中文路径\appcds\test.jsa", "-Xmx4G", false)]
    [DataRow(true, 21, true, true, ValidArchive, "-javaagent:authlib-injector.jar -Xmx4G", false)]
    [DataRow(true, 21, true, true, ValidArchive, "\"-javaagent:pcl-moon-startup-agent.jar\" -Xmx4G", false)]
    [DataRow(true, 21, true, true, ValidArchive, "-XX:SharedArchiveFile=custom.jsa -Xmx4G", false)]
    [DataRow(true, 21, true, true, ValidArchive, "-Xshare:off -Xmx4G", false)]
    [DataRow(true, 21, true, true, ValidArchive, "@options.txt -Xmx4G", false)]
    [DataRow(true, 21, true, true, ValidArchive, "\"unclosed -Xmx4G", false)]
    public void EvaluateRespectsCompatibilityAndToggle(
        bool enabled, int javaMajor, bool is64Bit, bool hasBaseArchive,
        string archivePath, string arguments, bool expected)
    {
        var decision = AppCdsPolicy.Evaluate(enabled, javaMajor, is64Bit, hasBaseArchive, archivePath, arguments);
        Assert.AreEqual(expected, decision.Enabled, decision.Reason);
    }

    [TestMethod]
    public void ArchiveFileNameIsDeterministicAndNormalized()
    {
        var a = AppCdsPolicy.ArchiveFileName(@"C:\Java\bin\java.exe", @"D:\Game\versions\SRE4.3");
        var b = AppCdsPolicy.ArchiveFileName(@"c:/java/bin/java.exe", @"d:/game/versions/sre4.3/");
        var c = AppCdsPolicy.ArchiveFileName(@"C:\Java\bin\java.exe", @"D:\Game\versions\Other");
        Assert.AreEqual(a, b);
        Assert.AreNotEqual(a, c);
        Assert.IsTrue(a.EndsWith(".jsa"));
    }

    [TestMethod]
    public void BuildArgumentsFormatsSharedArchiveFlags()
    {
        var built = AppCdsPolicy.BuildArguments(ValidArchive);
        Assert.AreEqual("-XX:+AutoCreateSharedArchive -XX:SharedArchiveFile=\"" + ValidArchive + "\" ", built);
    }
}
