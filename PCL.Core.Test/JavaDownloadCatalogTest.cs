using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Java;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace PCL.Core.Test
{
    [TestClass]
    public class JavaDownloadCatalogTest
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private const string PackageName = "zulu21.40.17-ca-jdk21.0.6-win_x64.zip";

        [TestMethod]
        [DataRow(true, "x64")]
        [DataRow(false, "i686")]
        public void ZuluCandidates_SelectsArchitectureAndExcludesSpecialPackages(bool is64Bit, string architecture)
        {
            var packages = new JsonArray();
            string[] names =
            [
                "zulu21.40.17-ca-jdk21.0.6-win_x64.zip",
                "zulu21.40.17-ca-jdk21.0.6-win_i686.zip",
                "zulu21.40.17-ca-jdk21.0.6-win_aarch64.zip",
                $"zulu21.40.17-ca-crac-jdk21.0.6-win_{architecture}.zip",
                $"zulu21.40.17-ca-fx-jdk21.0.6-win_{architecture}.zip",
                $"zulu21.40.17-ca-jre21.0.6-win_{architecture}.zip"
            ];
            for (var index = 0; index < names.Length; index++)
                packages.Add(Candidate(index + 1, names[index], 21, 0, 6));

            CollectionAssert.AreEqual(new[] { Id(is64Bit ? 1 : 2) },
                JavaDownloadCatalog.ZuluCandidates(packages, new JavaDownloadRequest(21, is64Bit)));
        }

        [TestMethod]
        public void ZuluCandidates_SortsNumericallyIncludingFourthComponent()
        {
            var packages = new JsonArray(
                Candidate(1, PackageName, 21, 0, 9),
                Candidate(2, PackageName, 21, 0, 10, 2),
                Candidate(3, PackageName, 21, 0, 10, 10),
                Candidate(4, PackageName, 21, 0, 10, 3, 99));

            CollectionAssert.AreEqual(new[] { Id(3), Id(4), Id(2), Id(1) },
                JavaDownloadCatalog.ZuluCandidates(packages, new JavaDownloadRequest(21, true)));
        }

        [TestMethod]
        public void ZuluCandidates_RejectsInvalidVersionsIdsAndOutOfRangePackages()
        {
            var invalidId = Candidate(1, PackageName, 21, 0, 6);
            invalidId["package_uuid"] = "not-a-guid";
            var packages = new JsonArray(
                invalidId,
                Candidate(2, PackageName, 21, 0),
                Candidate(3, PackageName, 21, 0, -1),
                Candidate(4, PackageName, 17, 0, 6),
                Candidate(5, PackageName, 21, 0, 5),
                Candidate(6, PackageName, 21, 0, 7),
                Candidate(7, PackageName, 21, 0, 6),
                null,
                "unexpected");
            var request = new JavaDownloadRequest(21, true, new Version(21, 0, 6), new Version(21, 0, 6));

            CollectionAssert.AreEqual(new[] { Id(7) }, JavaDownloadCatalog.ZuluCandidates(packages, request));
        }

        [TestMethod]
        [DataRow("1.8.0.350", false)]
        [DataRow("1.8.0.351", true)]
        [DataRow("8.0.351", true)]
        [DataRow("1.8.0.352", true)]
        [DataRow("8.0.352", true)]
        [DataRow("8.0.353", false)]
        [DataRow("17.0.1", false)]
        public void Request_AcceptsNormalizedJava8WithinInclusiveBounds(string version, bool expected)
        {
            var request = new JavaDownloadRequest(8, true, new Version(1, 8, 0, 351), new Version(8, 0, 352));
            Assert.AreEqual(expected, request.Accepts(Version.Parse(version)));
        }

        [TestMethod]
        public void Request_CompatibleMajorsRestrictsRequestedMajor()
        {
            var request = new JavaDownloadRequest(8, true, CompatibleMajors: new HashSet<int> { 8, 17 });
            Assert.IsTrue(request.Accepts(new Version(1, 8, 0, 351)));
            Assert.IsFalse(request.Accepts(new Version(17, 0, 1)));
            Assert.IsFalse((request with { CompatibleMajors = new HashSet<int> { 17 } }).Accepts(new Version(8, 0, 351)));
            Assert.IsFalse((request with { CompatibleMajors = new HashSet<int>() }).Accepts(new Version(8, 0, 351)));
            Assert.IsTrue((request with { CompatibleMajors = null }).Accepts(new Version(8, 0, 351)));
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ReadZuluPackage_AcceptsMatchingStandardJdk(bool is64Bit)
        {
            var item = Package();
            if (!is64Bit)
            {
                item["name"] = PackageName.Replace("x64", "i686");
                item["download_url"] = "https://cdn.azul.com/zulu/bin/" + item["name"]!.GetValue<string>();
                item["hw_bitness"] = 32;
            }
            item["sha256_hash"] = Hash.ToUpperInvariant();

            var result = JavaDownloadCatalog.ReadZuluPackage(item, new JavaDownloadRequest(21, is64Bit));

            Assert.AreEqual("Zulu", result.Vendor);
            Assert.AreEqual(item["name"]!.GetValue<string>(), result.Name);
            Assert.AreEqual(item["download_url"]!.GetValue<string>(), result.DownloadUrl);
            Assert.AreEqual(Hash, result.Sha256);
            Assert.AreEqual(123456L, result.Size);
        }

        [TestMethod]
        [DataRow("os", "\"linux\"")]
        [DataRow("arch", "\"aarch64\"")]
        [DataRow("hw_bitness", "32")]
        [DataRow("archive_type", "\"msi\"")]
        [DataRow("java_package_type", "\"jre\"")]
        [DataRow("availability_type", "\"SA\"")]
        [DataRow("release_status", "\"ea\"")]
        [DataRow("javafx_bundled", "true")]
        [DataRow("crac_supported", "true")]
        [DataRow("name", "\"zulu21.40.17-ca-jdk21.0.6-win_i686.zip\"")]
        [DataRow("java_version", "[17,0,6]")]
        [DataRow("java_version", "[21,0]")]
        [DataRow("java_version", "[21,0,-1]")]
        [DataRow("sha256_hash", "\"invalid\"")]
        [DataRow("sha256_hash", "null")]
        [DataRow("size", "0")]
        [DataRow("size", "-1")]
        [DataRow("size", "null")]
        public void ReadZuluPackage_RejectsInvalidMetadata(string property, string jsonValue)
        {
            var item = Package();
            item[property] = JsonNode.Parse(jsonValue);
            Assert.ThrowsExactly<InvalidDataException>(() =>
                JavaDownloadCatalog.ReadZuluPackage(item, new JavaDownloadRequest(21, true)));
        }

        [TestMethod]
        [DataRow("http://cdn.azul.com/zulu/bin/" + PackageName)]
        [DataRow("https://cdn.azul.com.evil.example/zulu/bin/" + PackageName)]
        [DataRow("https://evil.example/zulu/bin/" + PackageName)]
        [DataRow("https://cdn.azul.com/other/" + PackageName)]
        [DataRow("https://cdn.azul.com/zulu/bin/other.zip")]
        [DataRow("https://cdn.azul.com/zulu/bin/" + PackageName + "?redirect=1")]
        [DataRow("not-a-url")]
        public void ReadZuluPackage_RejectsUnexpectedDownloadUrl(string url)
        {
            var item = Package();
            item["download_url"] = url;
            Assert.ThrowsExactly<InvalidDataException>(() =>
                JavaDownloadCatalog.ReadZuluPackage(item, new JavaDownloadRequest(21, true)));
        }

        [TestMethod]
        public void ReadZuluPackage_RejectsVersionOutsideRequestedBounds()
        {
            Assert.ThrowsExactly<InvalidDataException>(() => JavaDownloadCatalog.ReadZuluPackage(Package(),
                new JavaDownloadRequest(21, true, new Version(21, 0, 7))));
            Assert.ThrowsExactly<InvalidDataException>(() => JavaDownloadCatalog.ReadZuluPackage(Package(),
                new JavaDownloadRequest(21, true, MaximumVersion: new Version(21, 0, 5))));
        }

        [TestMethod]
        [DataRow(8, false)]
        [DataRow(8, true)]
        [DataRow(11, false)]
        [DataRow(11, true)]
        [DataRow(17, false)]
        [DataRow(17, true)]
        [DataRow(21, true)]
        [DataRow(25, true)]
        [DataRow(26, true)]
        public void Injdk_SelectsPinnedStandardJdk(int major, bool is64Bit)
        {
            var package = InjdkJavaCatalog.SelectPackage(new JavaDownloadRequest(major, is64Bit));
            Assert.IsNotNull(package);
            Assert.AreEqual("Zulu", package.Vendor);
            StringAssert.StartsWith(package.DownloadUrl, $"https://d10.injdk.cn/openjdk/zulu/{major}/");
            StringAssert.EndsWith(package.Name, is64Bit ? "-win_x64.zip" : "-win_i686.zip");
            StringAssert.Contains(package.Name, "-ca-jdk");
            Assert.AreEqual(package.Sha256, JavaDownloadCatalog.ReadSha256(package.Sha256));
            Assert.AreEqual(-1L, package.Size);
        }

        [TestMethod]
        public void Injdk_AllowsOlderCompatibleUpdateWithoutOfficialMetadata()
        {
            var package = InjdkJavaCatalog.SelectPackage(new JavaDownloadRequest(21, true,
                new Version(21, 0, 0), new Version(21, 0, 12)));
            Assert.IsNotNull(package);
            Assert.AreEqual("zulu21.50.19-ca-jdk21.0.11-win_x64.zip", package.Name);
            Assert.AreEqual("84c73a4dcf8400ac01c1fe2d622cc319199e93c1856c200cb50c1bc16dc7dc97", package.Sha256);
        }

        [TestMethod]
        [DataRow(7, true)]
        [DataRow(16, true)]
        [DataRow(21, false)]
        [DataRow(25, false)]
        [DataRow(27, true)]
        public void Injdk_DoesNotSubstituteOtherMajorsArchitecturesOrPreview(int major, bool is64Bit)
        {
            Assert.IsNull(InjdkJavaCatalog.SelectPackage(new JavaDownloadRequest(major, is64Bit)));
        }

        [TestMethod]
        public void Injdk_StillHonorsVersionBoundsAndCompatibleMajors()
        {
            Assert.IsNull(InjdkJavaCatalog.SelectPackage(new JavaDownloadRequest(21, true,
                MinimumVersion: new Version(21, 0, 12))));
            Assert.IsNull(InjdkJavaCatalog.SelectPackage(new JavaDownloadRequest(21, true,
                MaximumVersion: new Version(21, 0, 10))));
            Assert.IsNull(InjdkJavaCatalog.SelectPackage(new JavaDownloadRequest(11, true,
                CompatibleMajors: new HashSet<int> { 8, 17 })));
            Assert.IsNotNull(InjdkJavaCatalog.SelectPackage(new JavaDownloadRequest(8, true,
                new Version(1, 8, 0, 492), new Version(1, 8, 0, 492))));
            Assert.IsNull(InjdkJavaCatalog.SelectPackage(new JavaDownloadRequest(8, true,
                MaximumVersion: new Version(1, 8, 0, 321))));
        }

        [TestMethod]
        [DataRow(Hash)]
        [DataRow("  \r\n" + Hash + "\r\n")]
        [DataRow(Hash + "  jdk.zip\n")]
        [DataRow(Hash + " *jdk.zip\r\n")]
        [DataRow("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
        public void ReadSha256_AcceptsHexAndSha256SumFormat(string text)
        {
            Assert.AreEqual(text.Trim()[..64].ToLowerInvariant(), JavaDownloadCatalog.ReadSha256(text));
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("<html><body>" + Hash + "</body></html>")]
        [DataRow("<!DOCTYPE html><html>Access Denied</html>")]
        [DataRow("0123456789abcdef")]
        [DataRow(Hash + "0")]
        [DataRow("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
        public void ReadSha256_RejectsHtmlAndMalformedHash(string text)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => JavaDownloadCatalog.ReadSha256(text));
        }

        private static string Id(int value) => $"00000000-0000-0000-0000-{value:D12}";

        private static JsonObject Candidate(int id, string name, params int[] version)
        {
            var parts = new JsonArray();
            foreach (var part in version) parts.Add(part);
            return new JsonObject
            {
                ["package_uuid"] = Id(id),
                ["name"] = name,
                ["java_version"] = parts
            };
        }

        private static JsonObject Package() => new()
        {
            ["name"] = PackageName,
            ["java_version"] = new JsonArray(21, 0, 6),
            ["os"] = "windows",
            ["arch"] = "x86",
            ["hw_bitness"] = 64,
            ["archive_type"] = "zip",
            ["java_package_type"] = "jdk",
            ["availability_type"] = "CA",
            ["release_status"] = "ga",
            ["javafx_bundled"] = false,
            ["crac_supported"] = false,
            ["download_url"] = "https://cdn.azul.com/zulu/bin/" + PackageName,
            ["sha256_hash"] = Hash,
            ["size"] = 123456L
        };


    }
}
