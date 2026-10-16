using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Multiplayer;

namespace PCL.Core.Test.Multiplayer;

[TestClass]
public sealed class ZeroTierClientTest
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request);
    }
    private static HttpResponseMessage Network(string status = "OK") => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new { name = "test", status, assignedAddresses = new[] { "10.23.0.2/24" } }) };
    [TestMethod]
    public async Task ExistingNetworkIsNeitherReconfiguredNorLeft()
    {
        var requests = new List<HttpMethod>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.Method); Assert.AreEqual("127.0.0.1", request.RequestUri!.Host);
            Assert.AreEqual("", request.RequestUri.Query); Assert.IsTrue(request.Headers.Contains("X-ZT1-Auth"));
            return Task.FromResult(Network());
        }));
        using var client = new ZeroTierClient("local-private-token", http);
        var lease = await client.JoinAsync("0123456789abcdef", CancellationToken.None);
        Assert.IsFalse(lease.CreatedByLauncher); Assert.AreEqual("10.23.0.2", lease.Info.Addresses[0]);
        await lease.DisposeAsync(); await lease.DisposeAsync();
        Assert.IsTrue(requests.TrueForAll(method => method == HttpMethod.Get));
    }
    [TestMethod]
    public async Task NewJoinDisablesGlobalDefaultAndDnsAndOnlyOwnedNetworkIsDeleted()
    {
        var exists = false; var deleted = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return exists ? Network() : new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.IsFalse(body.RootElement.GetProperty("allowDefault").GetBoolean());
                Assert.IsFalse(body.RootElement.GetProperty("allowGlobal").GetBoolean());
                Assert.IsFalse(body.RootElement.GetProperty("allowDNS").GetBoolean());
                exists = true; return Network();
            }
            deleted++; return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var client = new ZeroTierClient("local-private-token", http);
        var lease = await client.JoinAsync("0123456789abcdef", CancellationToken.None);
        Assert.IsTrue(lease.CreatedByLauncher);
        await lease.DisposeAsync(); await lease.DisposeAsync(); Assert.AreEqual(1, deleted);
    }
    [TestMethod]
    public async Task AuthorizationPendingIsNotTreatedAsConnectedAndFailedNewJoinIsCleaned()
    {
        var count = 0; var deleted = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Delete) { deleted++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
            if (count++ == 0) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(Network("ACCESS_DENIED"));
        }));
        using var client = new ZeroTierClient("local-private-token", http);
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => client.JoinAsync("0123456789abcdef", CancellationToken.None));
        StringAssert.Contains(error.Message, "授权"); Assert.AreEqual(1, deleted);
        Assert.IsFalse(error.ToString().Contains("local-private-token"));
    }
    [TestMethod]
    public async Task InvalidNetworkNeverReachesLocalApi()
    {
        using var http = new HttpClient(new Handler(_ => throw new AssertFailedException("No network expected")));
        using var client = new ZeroTierClient("token", http);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.GetNetworkAsync("../status", CancellationToken.None));
    }
}
