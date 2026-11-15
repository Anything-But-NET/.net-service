using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Adapters
{
    public static class GraphClientFactory
    {
        public static GraphServiceClient Create(IConfiguration? configuration)
        {
            var tenantId = configuration?["GraphApi:TenantId"];
            var clientId = configuration?["GraphApi:ClientId"];
            var clientSecret = configuration?["GraphApi:ClientSecret"];
            var configuredBaseUrl = configuration?["GraphApi:BaseUrl"];

            if (!string.IsNullOrWhiteSpace(tenantId) &&
                !string.IsNullOrWhiteSpace(clientId) &&
                !string.IsNullOrWhiteSpace(clientSecret))
            {
                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

                if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
                {
                    graphClient.RequestAdapter.BaseUrl = configuredBaseUrl;
                }

                return graphClient;
            }

            var baseUrl = !string.IsNullOrWhiteSpace(configuredBaseUrl)
                ? configuredBaseUrl
                : "http://mock-graph:8080/v1.0";

            var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider());
            var adapter = new HttpClientRequestAdapter(auth) { BaseUrl = baseUrl };
            return new GraphServiceClient(adapter);
        }
    }
}
