using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Profile.Authentication;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class MicrosoftTokenRequestTest
{
    [TestMethod]
    [DataRow("refresh_token", "refresh_token", "XboxLive.signin offline_access")]
    [DataRow("urn:ietf:params:oauth:grant-type:device_code", "device_code", null)]
    public async Task PublicClientTokenRequestsNeverSendSecret(string grantType, string credentialName, string? scope)
    {
        var provider = new MicrosoftProvider("public-client-id");
        const string credential = "credential+with/reserved=&characters";
        using var request = provider.CreateTokenRequest(grantType, credentialName, credential, scope);

        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", request.RequestUri!.AbsoluteUri);
        Assert.AreEqual("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        Assert.IsNull(request.Headers.Authorization);
        var body = await request.Content.ReadAsStringAsync();
        var fields = body.Split('&').Select(part => part.Split('=', 2)).ToDictionary(
            pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));

        Assert.AreEqual("public-client-id", fields["client_id"]);
        Assert.AreEqual(grantType, fields["grant_type"]);
        Assert.AreEqual(credential, fields[credentialName]);
        Assert.IsFalse(fields.ContainsKey("client_secret"));
        Assert.AreEqual(scope is null ? 3 : 4, fields.Count);
        if (scope is not null) Assert.AreEqual(scope, fields["scope"]);
        else Assert.IsFalse(fields.ContainsKey("scope"));
    }
}
