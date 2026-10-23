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
}
