using Copilot.IngestionWorker.Models;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text;

namespace Copilot.IngestionWorker.Adapters
{
    public class MockSharePointAdapter : IMockSharePointAdapter
    {
        private readonly GraphServiceClient _graphClient;

        public MockSharePointAdapter()
        {
            var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider());
            var adapter = new HttpClientRequestAdapter(auth) { BaseUrl = "http://mock-graph:8080/v1.0" };
            _graphClient = new GraphServiceClient(adapter);
        }

        public async Task<IEnumerable<MockDocument>> GetDocumentsAsync(CancellationToken cancellationToken)
        {
            var mockDocuments = new List<MockDocument>();

            try
            {
                var sites = await _graphClient.Sites.GetAsync(cancellationToken: cancellationToken);
                var siteId = sites?.Value?.FirstOrDefault()?.Id;
                if (siteId == null) return mockDocuments;

                var drives = await _graphClient.Sites[siteId].Drives.GetAsync(cancellationToken: cancellationToken);
                var driveId = drives?.Value?.FirstOrDefault()?.Id;
                if (driveId == null) return mockDocuments;

                var delta = await _graphClient.Drives[driveId].Items["root"].Delta.GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken);

                if (delta?.Value != null)
                {
                    foreach (var item in delta.Value)
                    {
                        if (item.File == null || item.Deleted != null) continue;

                        using var contentStream = await _graphClient.Drives[driveId].Items[item.Id].Content.GetAsync(cancellationToken: cancellationToken);
                        using var reader = new StreamReader(contentStream!);
                        var contentText = await reader.ReadToEndAsync(cancellationToken: cancellationToken);

                        mockDocuments.Add(new MockDocument(
                            Id: item.Id!,
                            Title: item.Name ?? "Без назви",
                            Content: contentText,
                            SourceUrl: item.WebUrl ?? "",
                            CreatedAt: item.CreatedDateTime?.DateTime ?? DateTime.UtcNow
                        ));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Помилка Graph API: {ex.Message}");
            }

            return mockDocuments;
        }
    }
}
