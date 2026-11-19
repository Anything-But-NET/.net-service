using System.Net;
using System.Text;
using System.Text.Json;
using Copilot.Contracts;
using Copilot.IngestionWorker.Adapters;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Moq;
using Moq.Protected;
using GraphClientFactory = Copilot.IngestionWorker.Adapters.GraphClientFactory;

namespace Copilot.IngestionWorker.Tests
{
    public class GraphAdaptersTests
    {
        private static GraphServiceClient CreateMockGraphClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => responder(req));

            var httpClient = new HttpClient(handlerMock.Object);
            var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider());
            var adapter = new HttpClientRequestAdapter(auth, httpClient: httpClient)
            {
                BaseUrl = "http://mock-graph:8080/v1.0"
            };

            return new GraphServiceClient(adapter);
        }

        private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        [Fact]
        public void GraphClientFactory_RealCredentialsWithHttpMockBaseUrl_IgnoresInsecureBaseUrl()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GraphApi:BaseUrl"] = "http://mock-graph:8080/v1.0",
                    ["GraphApi:TenantId"] = "247832a5-9824-4eb9-9138-b41e57e93e9d",
                    ["GraphApi:ClientId"] = "111404d6-ff41-4b12-981b-0a8142510842",
                    ["GraphApi:ClientSecret"] = "secret-value"
                })
                .Build();

            var client = GraphClientFactory.Create(config);

            Assert.StartsWith("https://graph.microsoft.com", client.RequestAdapter.BaseUrl);
        }

        [Fact]
        public void GraphClientFactory_NoCredentials_FallsBackToMockGraph()
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GraphApi:BaseUrl"] = "",
                    ["GraphApi:TenantId"] = "",
                    ["GraphApi:ClientId"] = "",
                    ["GraphApi:ClientSecret"] = ""
                })
                .Build();

            var client = GraphClientFactory.Create(config);

            Assert.Equal("http://mock-graph:8080/v1.0", client.RequestAdapter.BaseUrl);
        }

        [Fact]
        public async Task GraphUserAdapter_PaginatesUsersAndGroups_AndFailsClosedOnError()
        {
            var produced = new List<KafkaUserAclUpsert>();
            var producerMock = new Mock<ITopicProducer<KafkaUserAclUpsert>>();
            producerMock
                .Setup(p => p.Produce(It.IsAny<KafkaUserAclUpsert>(), It.IsAny<CancellationToken>()))
                .Callback<KafkaUserAclUpsert, CancellationToken>((e, _) => produced.Add(e))
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(producerMock.Object);
            var provider = services.BuildServiceProvider();

            var graphClient = CreateMockGraphClient(req =>
            {
                var url = req.RequestUri!.ToString();

                if (url.EndsWith("/v1.0/users"))
                {
                    return JsonResponse("""
                    {
                      "value": [{ "id": "u1", "mail": "alice@lpnu.ua" }],
                      "@odata.nextLink": "http://mock-graph:8080/v1.0/users?page=2"
                    }
                    """);
                }

                if (url.Contains("/users?page=2"))
                {
                    return JsonResponse("""
                    {
                      "value": [{ "id": "u2", "mail": "bob@lpnu.ua" }]
                    }
                    """);
                }

                if (url.EndsWith("/users/u1/transitiveMemberOf"))
                {
                    return JsonResponse("""
                    {
                      "value": [
                        { "@odata.type": "#microsoft.graph.directoryRole", "id": "role-1" },
                        { "@odata.type": "#microsoft.graph.group", "id": "g1", "displayName": "RBAC-Engineering" }
                      ]
                    }
                    """);
                }

                if (url.EndsWith("/users/u2/transitiveMemberOf"))
                {
                    return JsonResponse("""{"error":{"code":"TooManyRequests"}}""", HttpStatusCode.TooManyRequests);
                }

                return JsonResponse("{}", HttpStatusCode.NotFound);
            });

            var adapter = new GraphUserAdapter(provider, NullLogger<GraphUserAdapter>.Instance, graphClient);
            await adapter.SyncUsersAndGroupsAsync(CancellationToken.None);

            var aliceEvent = Assert.Single(produced);
            Assert.Equal("alice@lpnu.ua", aliceEvent.Email);
            Assert.Equal(new[] { "RBAC-Engineering" }, aliceEvent.Acl.AllowedGroups);
        }

        [Fact]
        public async Task MockSharePointAdapter_IsolatesSiteErrors_ResolvesUserEmail_AndPreventsOwnerOverGranting()
        {
            var graphClient = CreateMockGraphClient(req =>
            {
                var url = req.RequestUri!.ToString();

                if (url.EndsWith("/v1.0/sites"))
                {
                    return JsonResponse("""
                    {
                      "value": [
                        { "id": "site-forbidden", "displayName": "Secret Site" },
                        { "id": "site-team", "displayName": "Anything But .NET" }
                      ]
                    }
                    """);
                }

                if (url.Contains("search=*"))
                    return JsonResponse("""{"value":[]}""");

                if (url.Contains("/sites/site-forbidden/drives"))
                    return JsonResponse("{}", HttpStatusCode.Forbidden);

                if (url.Contains("/sites/site-team/drives"))
                    return JsonResponse("""{"value":[{"id":"drive-1","name":"Documents"}]}""");

                if (url.Contains("/drives/drive-1/items/root/delta"))
                {
                    return JsonResponse("""
                    {
                      "value": [
                        {
                          "id": "doc-secret",
                          "name": "secret.md",
                          "eTag": "etag-1",
                          "file": { "mimeType": "text/markdown" }
                        }
                      ],
                      "@odata.deltaLink": "http://mock-graph:8080/v1.0/drives/drive-1/items/root/delta?token=abc"
                    }
                    """);
                }

                if (url.Contains("/items/doc-secret/permissions"))
                {
                    return JsonResponse("""
                    {
                      "value": [
                        {
                          "roles": ["write"],
                          "grantedToV2": { "siteGroup": { "id": "3", "displayName": "Власники Anything But .NET" } }
                        },
                        {
                          "roles": ["read"],
                          "grantedToV2": { "user": { "id": "u-guid-1", "displayName": "John Doe", "email": "john@lpnu.ua" } }
                        }
                      ]
                    }
                    """);
                }

                if (url.Contains("/items/doc-secret/content"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("confidential text") };

                return JsonResponse("{}", HttpStatusCode.NotFound);
            });

            var adapter = new MockSharePointAdapter(graphClient, logger: NullLogger<MockSharePointAdapter>.Instance);
            var docs = (await adapter.GetDocumentsAsync(CancellationToken.None)).ToList();

            var doc = Assert.Single(docs);
            Assert.Contains("Власники Anything But .NET", doc.AllowedGroups!);
            Assert.Contains("john@lpnu.ua", doc.AllowedGroups!);
            Assert.DoesNotContain("John Doe", doc.AllowedGroups!);
            Assert.DoesNotContain("Anything But .NET", doc.AllowedGroups!);
        }

        [Fact]
        public async Task MockSharePointAdapter_DoesNotAdvanceDeltaLinkWhenDocumentSyncFails_AndDetectsAclOnlyChange()
        {
            int contentDownloadCount = 0;
            int deltaCallCount = 0;
            bool returnUpdatedAcl = false;

            var graphClient = CreateMockGraphClient(req =>
            {
                var url = req.RequestUri!.ToString();

                if (url.EndsWith("/v1.0/sites"))
                    return JsonResponse("""{"value":[{"id":"site-1","displayName":"Anything But .NET"}]}""");

                if (url.Contains("search=*"))
                    return JsonResponse("""{"value":[]}""");

                if (url.Contains("/sites/site-1/drives"))
                    return JsonResponse("""{"value":[{"id":"drive-1","name":"Documents"}]}""");

                if (url.Contains("/drives/drive-1/items/root/delta"))
                {
                    deltaCallCount++;
                    return JsonResponse("""
                    {
                      "value": [
                        {
                          "id": "doc-1",
                          "name": "policy.md",
                          "eTag": "etag-v1",
                          "file": { "mimeType": "text/markdown" }
                        }
                      ],
                      "@odata.deltaLink": "http://mock-graph:8080/v1.0/drives/drive-1/items/root/delta?token=page2"
                    }
                    """);
                }

                if (url.Contains("/items/doc-1/permissions"))
                {
                    var groupName = returnUpdatedAcl ? "RBAC-Finance" : "RBAC-Engineering";
                    return JsonResponse($$"""
                    {
                      "value": [
                        { "roles": ["read"], "grantedToV2": { "group": { "id": "g1", "displayName": "{{groupName}}" } } }
                      ]
                    }
                    """);
                }

                if (url.Contains("/items/doc-1/content"))
                {
                    contentDownloadCount++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("policy content") };
                }

                return JsonResponse("{}", HttpStatusCode.NotFound);
            });

            var adapter = new MockSharePointAdapter(graphClient, logger: NullLogger<MockSharePointAdapter>.Instance);

            var firstRun = (await adapter.GetDocumentsAsync(CancellationToken.None)).ToList();
            Assert.Single(firstRun);
            adapter.CompleteSync(new HashSet<string> { "doc-1" });

            var secondRun = (await adapter.GetDocumentsAsync(CancellationToken.None)).ToList();
            var doc2 = Assert.Single(secondRun);
            doc2.OnSynced?.Invoke();
            adapter.CompleteSync(new HashSet<string>());
            Assert.Equal(2, contentDownloadCount);

            returnUpdatedAcl = true;
            var thirdRun = (await adapter.GetDocumentsAsync(CancellationToken.None)).ToList();
            var doc3 = Assert.Single(thirdRun);
            Assert.Contains("RBAC-Finance", doc3.AllowedGroups!);
            Assert.Equal(2, contentDownloadCount);
            Assert.True(deltaCallCount == 3);
        }
    }
}