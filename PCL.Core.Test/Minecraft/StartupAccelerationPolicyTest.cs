using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Launcher;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class StartupAccelerationPolicyTest
{
    [TestMethod]
    [DataRow(21, true, "", true)]
    [DataRow(21, true, "-Xmx4G", true)]
    [DataRow(17, true, "-Xmx4G", true)]
    [DataRow(8, true, "-Xmx4G", false)]
    [DataRow(21, false, "-Xmx4G", false)]
    [DataRow(21, true, "-Dpclmoon.startup.disabled=true", false)]
    [DataRow(17, true, "-Dpclmoon.startup.disabled=true", false)]
    [DataRow(21, true, "-Dmooncl.startup.disabled=true", false)]
    [DataRow(21, true, "@options.txt", false)]
    [DataRow(21, true, "-XX:Flags=options.txt", false)]
    [DataRow(21, true, "\"unclosed", false)]
    [DataRow(21, true, "-javaagent:pcl-moon-startup-agent.jar", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true", true)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=TRUE", true)]
    [DataRow(21, true, "\"-Dpclmoon.startup.enabled=true\"", true)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=\"true\"", true)]
    [DataRow(17, true, "-Dpclmoon.startup.enabled=true", true)]
    [DataRow(17, true, "-Dpclmoon.startup.enabled=true -Dpclmoon.startup.disabled=true", false)]
    [DataRow(17, false, "-Dpclmoon.startup.enabled=true", false)]
    [DataRow(17, true, "-Dpclmoon.startup.enabled=true @options.txt", false)]
    [DataRow(8, true, "-Dpclmoon.startup.enabled=true", false)]
    [DataRow(18, true, "-Dpclmoon.startup.enabled=true", false)]
    [DataRow(25, true, "-Dpclmoon.startup.enabled=true", false)]
    [DataRow(21, false, "-Dpclmoon.startup.enabled=true", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=false", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true -Dpclmoon.startup.enabled=false", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=false -Dpclmoon.startup.enabled=true", true)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true -Dpclmoon.startup.disabled=true", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true -Dpclmoon.startup.disabled=TRUE", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true -Dpclmoon.startup.disabled=true -Dpclmoon.startup.disabled=false", true)]
    [DataRow(21, true, "-Dnote=\"example -Dpclmoon.startup.enabled=false\"", true)]
    [DataRow(21, true, "-Dnote=\"example -Dpclmoon.startup.disabled=true\"", true)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true @options.txt", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true -XX:Flags=options.txt", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true \"unclosed", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true -javaagent:\"C:/a b/pcl-moon-startup-agent-hash.jar\"", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true", true)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=TRUE", true)]
    [DataRow(21, true, "\"-Dmooncl.startup.enabled=true\"", true)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true -Dmooncl.startup.disabled=true", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true -Dpclmoon.startup.disabled=true", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true -Dmooncl.startup.disabled=true", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=false", false)]
    [DataRow(21, true, "-Dmooncl.startup.enabled=true -javaagent:\"C:/a b/mooncl-startup-agent-hash.jar\"", false)]
    [DataRow(21, true, "-Dpclmoon.startup.enabled=true -javaagent:\"C:/a b/mooncl-startup-agent-hash.jar\"", false)]
    public void DefaultsOnWithCompatibilityAndExplicitOverrides(int java, bool is64Bit, string arguments, bool expected)
    {
        Assert.AreEqual(expected, StartupAccelerationPolicy.ShouldInject(java, is64Bit, arguments, hasForge: true));
    }

    [TestMethod]
    [DataRow(17, false, false)]
    [DataRow(17, true, true)]
    [DataRow(21, false, true)]
    [DataRow(21, true, true)]
    public void Java17RequiresForge(int java, bool hasForge, bool expected)
    {
        Assert.AreEqual(expected, StartupAccelerationPolicy.ShouldInject(java, true,
            "-Xmx4G", hasForge));
    }
}
