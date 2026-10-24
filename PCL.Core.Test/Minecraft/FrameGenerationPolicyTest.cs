using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class FrameGenerationPolicyTest
{
    [TestMethod]
    [DataRow(false, false, 21, true, false)]
    [DataRow(true, false, 8, true, true)]
    [DataRow(false, true, 17, true, true)]
    [DataRow(true, true, 25, true, true)]
    [DataRow(true, true, 7, true, false)]
    [DataRow(true, true, 21, false, false)]
    public void OnlyOptedInSupportedJvmsAreInjected(bool generation, bool diagnostics, int java, bool x64, bool expected)
        => Assert.AreEqual(expected, FrameGenerationPolicy.ShouldInject(generation, diagnostics, java, x64, ""));

    [TestMethod]
    [DataRow("-Dpclmoon.framegen.disabled=true", false)]
    [DataRow("\"-javaagent:D:/含 空格/pcl-moon-framegen-agent.jar\"", false)]
    [DataRow("-javaagent:\"D:/含 空格/pcl-moon-framegen-agent.jar\"", false)]
    [DataRow("-javaagent:D:/other.jar", true)]
    [DataRow("-Dnote=\"example -Dpclmoon.framegen.disabled=true\"", true)]
    [DataRow("-Dnote=\"example -javaagent:D:/framegen.jar\"", true)]
    [DataRow("\"unclosed", false)]
    public void ExplicitDisableAndDuplicateAgentsAreRespected(string args, bool expected)
        => Assert.AreEqual(expected, FrameGenerationPolicy.ShouldInject(true, false, 21, true, args));

    [TestMethod]
    [DataRow(8, true, "", true)]
    [DataRow(21, true, "", true)]
    [DataRow(7, true, "", false)]
    [DataRow(21, false, "", false)]
    [DataRow(21, true, "-Dpclmoon.framegen.disabled=true", false)]
    [DataRow(21, true, "-javaagent:D:/pcl-framegen.jar", false)]
    public void SettingsButtonCanLoadAgentWithoutEnablingGeneration(int java, bool x64, string args, bool expected)
        => Assert.AreEqual(expected, FrameGenerationPolicy.ShouldInject(false, false, java, x64, args, settingsButton: true));

    [TestMethod]
    public void IndependentPacingLoadsWithoutGenerationButRespectsForcedDisable()
    {
        Assert.IsTrue(FrameGenerationPolicy.ShouldInject(false, false, 21, true, "", independentPacing: true));
        Assert.IsFalse(FrameGenerationPolicy.ShouldInject(false, false, 21, true,
            "-Dpclmoon.framegen.disabled=true", independentPacing: true));
    }

    [TestMethod]
    public void StandaloneSuperResolutionLoadsWithoutGenerationButRespectsForcedDisable()
    {
        Assert.IsTrue(FrameGenerationPolicy.ShouldInject(false, false, 21, true, "", superResolution: true));
        Assert.IsTrue(FrameGenerationPolicy.ShouldInject(false, false, 8, true, "", superResolution: true));
        Assert.IsFalse(FrameGenerationPolicy.ShouldInject(false, false, 7, true, "", superResolution: true));
        Assert.IsFalse(FrameGenerationPolicy.ShouldInject(false, false, 21, false, "", superResolution: true));
        Assert.IsFalse(FrameGenerationPolicy.ShouldInject(false, false, 21, true,
            "-Dpclmoon.framegen.disabled=true", superResolution: true));
        Assert.IsFalse(FrameGenerationPolicy.ShouldInject(false, false, 21, true,
            "-javaagent:D:/pcl-framegen.jar", superResolution: true));
        Assert.IsFalse(FrameGenerationPolicy.ShouldInject(false, false, 21, true, ""));
    }

    [TestMethod]
    public void ProfileIdentityIsInstanceBasedAndStableOnWindows()
    {
        var first = FrameGenerationPolicy.ProfileKey("D:/Game/versions/测试实例");
        Assert.AreEqual(first, FrameGenerationPolicy.ProfileKey("d:/game/versions/测试实例/"));
        Assert.AreNotEqual(first, FrameGenerationPolicy.ProfileKey("D:/Game/versions/另一个实例"));
        Assert.AreEqual(64, first.Length);
        Assert.IsFalse(first.Contains("测试实例", System.StringComparison.Ordinal));
    }
}
