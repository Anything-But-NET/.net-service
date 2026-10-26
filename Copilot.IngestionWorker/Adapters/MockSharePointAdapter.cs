using Copilot.IngestionWorker.Models;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Abstractions.Serialization;
using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text;

namespace Copilot.IngestionWorker.Adapters
{
    public class MockSharePointAdapter : IMockSharePointAdapter
    {
        private readonly GraphServiceClient _graphClient;
        private readonly HttpClient _httpClient = new();
        private string? _deltaLink;

        public MockSharePointAdapter()
        {
            var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider());
            var adapter = new HttpClientRequestAdapter(auth) { BaseUrl = "http://localhost:8080/v1.0" };
            _graphClient = new GraphServiceClient(adapter);
        }

        public async Task<IEnumerable<MockDocument>> GetDocumentsAsync(CancellationToken cancellationToken)
        {
            var mockDocuments = new List<MockDocument>();

            try
            {
                var sites = await _graphClient.Sites.GetAsync(cancellationToken: cancellationToken);
                var siteId = sites?.Value?.FirstOrDefault()?.Id;
                if (siteId == null) throw new InvalidOperationException("Mock SharePoint не повернув жодного сайту.");

                var drives = await _graphClient.Sites[siteId].Drives.GetAsync(cancellationToken: cancellationToken);
                var driveId = drives?.Value?.FirstOrDefault()?.Id;
                if (driveId == null) throw new InvalidOperationException("Mock SharePoint не повернув жодного диска.");
                
                Microsoft.Graph.Drives.Item.Items.Item.Delta.DeltaGetResponse? delta;

                if (string.IsNullOrWhiteSpace(_deltaLink))
                {
                    delta = await _graphClient.Drives[driveId].Items["root"].Delta
                        .GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken);
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, _deltaLink);
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "app");

                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    delta = System.Text.Json.JsonSerializer.Deserialize<
                        Microsoft.Graph.Drives.Item.Items.Item.Delta.DeltaGetResponse>(json);
                }

                var allItems = new List<Microsoft.Graph.Models.DriveItem>();

                while (delta != null)
                {
                    if (delta.Value != null)
                        allItems.AddRange(delta.Value);

                    if (string.IsNullOrWhiteSpace(delta.OdataNextLink))
                    {
                        _deltaLink = delta.OdataDeltaLink ?? _deltaLink;
                        break;
                    }

                    using var request = new HttpRequestMessage(
                        HttpMethod.Get, delta.OdataNextLink);

                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue(
                            "Bearer", "app");

                    using var response = await _httpClient.SendAsync(
                        request, cancellationToken);

                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync(
                        cancellationToken);

                    delta = System.Text.Json.JsonSerializer.Deserialize<
                        Microsoft.Graph.Drives.Item.Items.Item.Delta.DeltaGetResponse>(
                            json);
                }

                foreach (var item in allItems)
                {
                    if (item.Deleted != null)
                    {
                        mockDocuments.Add(new MockDocument(
                            Id: item.Id!,
                            Title: string.Empty,
                            Content: string.Empty,
                            SourceUrl: string.Empty,
                            LastModifiedAt: DateTime.UtcNow,
                            AllowedGroups: Array.Empty<string>(),
                            IsDeleted: true
                        ));

                        continue;
                    }

                    if (item.File == null)
                        continue;

                    using var contentStream = await _graphClient
                        .Drives[driveId].Items[item.Id].Content
                        .GetAsync(cancellationToken: cancellationToken);

                    using var reader = new StreamReader(contentStream!);
                    var contentText = await reader.ReadToEndAsync(cancellationToken);

                    var permissions = await _graphClient
                        .Drives[driveId].Items[item.Id].Permissions
                        .GetAsync(cancellationToken: cancellationToken);

                    var allowedGroups = permissions?.Value?
                        .SelectMany(permission => new[]
                        {
                            permission.GrantedToV2?.SiteGroup?.DisplayName,
                            permission.GrantedTo?.User?.DisplayName
                        })
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name!)
                        .Distinct()
                        .ToArray() ?? Array.Empty<string>();

                    mockDocuments.Add(new MockDocument(
                        Id: item.Id!,
                        Title: item.Name ?? "Без назви",
                        Content: contentText,
                        SourceUrl: item.WebUrl ?? "",
                        LastModifiedAt: item.LastModifiedDateTime?.DateTime
                            ?? DateTime.UtcNow,
                        AllowedGroups: allowedGroups
                    ));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Помилка синхронізації Mock SharePoint: {ex}");
                throw;
            }

            return mockDocuments;
        }
    }
}
