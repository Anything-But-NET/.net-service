using Copilot.IngestionWorker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System.Text;

namespace Copilot.IngestionWorker.Adapters
{
    public class MockSharePointAdapter : IMockSharePointAdapter
    {
        private readonly GraphServiceClient _graphClient;
        private readonly ILogger<MockSharePointAdapter> _logger;

        public MockSharePointAdapter(
            IConfiguration? configuration = null,
            ILogger<MockSharePointAdapter>? logger = null)
        {
            _graphClient = GraphClientFactory.Create(configuration);
            _logger = logger ?? NullLogger<MockSharePointAdapter>.Instance;
        }

        public async Task<IEnumerable<MockDocument>> GetDocumentsAsync(CancellationToken cancellationToken)
        {
            var mockDocuments = new List<MockDocument>();

            try
            {
                var sitesList = new List<Site>();

                var initialSites = await _graphClient.Sites.GetAsync(cancellationToken: cancellationToken);
                if (initialSites?.Value != null)
                {
                    sitesList.AddRange(initialSites.Value);
                }

                try
                {
                    var searchedSites = await _graphClient.Sites.GetAsync(config =>
                    {
                        config.QueryParameters.Search = "*";
                    }, cancellationToken);

                    if (searchedSites?.Value != null)
                    {
                        foreach (var s in searchedSites.Value)
                        {
                            if (!string.IsNullOrWhiteSpace(s.Id) && sitesList.All(existing => existing.Id != s.Id))
                            {
                                sitesList.Add(s);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Sites search=* not supported by this Graph endpoint, using default sites list.");
                }

                _logger.LogInformation("Found {SiteCount} SharePoint site(s).", sitesList.Count);

                foreach (var site in sitesList)
                {
                    if (string.IsNullOrWhiteSpace(site.Id))
                        continue;

                    var drives = await _graphClient.Sites[site.Id].Drives.GetAsync(cancellationToken: cancellationToken);
                    if (drives?.Value == null)
                        continue;

                    foreach (var drive in drives.Value)
                    {
                        if (string.IsNullOrWhiteSpace(drive.Id))
                            continue;

                        _logger.LogInformation("Scanning SharePoint site '{SiteName}', drive '{DriveName}' ({DriveId})...",
                            site.DisplayName ?? site.Name ?? site.Id,
                            drive.Name ?? drive.Id,
                            drive.Id);

                        var delta = await _graphClient.Drives[drive.Id].Items["root"].Delta
                            .GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken);

                        if (delta?.Value == null)
                            continue;

                        foreach (var item in delta.Value)
                        {
                            if (item.File == null || item.Deleted != null || string.IsNullOrWhiteSpace(item.Id))
                                continue;

                            using var contentStream = await _graphClient.Drives[drive.Id].Items[item.Id].Content
                                .GetAsync(cancellationToken: cancellationToken);
                            if (contentStream == null)
                                continue;

                            using var ms = new MemoryStream();
                            await contentStream.CopyToAsync(ms, cancellationToken);
                            var rawBytes = ms.ToArray();
                            var contentText = Encoding.UTF8.GetString(rawBytes);

                            var allowedGroups = await ResolveDocumentGroupsAsync(drive.Id, item, cancellationToken);

                            mockDocuments.Add(new MockDocument(
                                Id: item.Id,
                                Title: item.Name ?? "Untitled",
                                Content: contentText,
                                SourceUrl: item.WebUrl ?? "",
                                CreatedAt: item.LastModifiedDateTime?.UtcDateTime
                                           ?? item.CreatedDateTime?.UtcDateTime
                                           ?? DateTime.UtcNow,
                                AllowedGroups: allowedGroups,
                                RawBytes: rawBytes,
                                ContentType: item.File.MimeType
                            ));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Graph API SharePoint synchronization error.");
            }

            return mockDocuments;
        }

        private async Task<IReadOnlyList<string>> ResolveDocumentGroupsAsync(
            string driveId,
            DriveItem item,
            CancellationToken ct)
        {
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var permissions = await _graphClient.Drives[driveId].Items[item.Id!].Permissions
                    .GetAsync(cancellationToken: ct);

                if (permissions?.Value != null)
                {
                    foreach (var perm in permissions.Value)
                    {
                        var candidates = new List<string?>
                        {
                            perm.GrantedToV2?.Group?.DisplayName,
                            perm.GrantedToV2?.SiteGroup?.DisplayName,
                            perm.GrantedTo?.User?.DisplayName
                        };
                        if (perm.GrantedToIdentitiesV2 != null)
                        {
                            foreach (var identity in perm.GrantedToIdentitiesV2)
                            {
                                candidates.Add(identity.Group?.DisplayName);
                                candidates.Add(identity.SiteGroup?.DisplayName);
                                candidates.Add(identity.User?.DisplayName);
                            }
                        }
                        foreach(var name in candidates)
                        {
                            if (string.IsNullOrWhiteSpace(name))
                                continue;
                            var trimmed = name.Trim();
                            groups.Add(trimmed);
                            if (trimmed.StartsWith("Учасники ", StringComparison.OrdinalIgnoreCase))
                            {
                                groups.Add(trimmed["Учасники ".Length..].Trim());
                            }
                            else if (trimmed.EndsWith(" Members", StringComparison.OrdinalIgnoreCase))
                            {
                                groups.Add(trimmed[..^" Members".Length].Trim());
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Permissions endpoint not available for item {ItemId}, falling back to folder/item metadata.", item.Id);
            }

            if (groups.Count == 0 && !string.IsNullOrWhiteSpace(item.ParentReference?.Name) &&
                !string.Equals(item.ParentReference.Name, "root", StringComparison.OrdinalIgnoreCase))
            {
                groups.Add(item.ParentReference.Name);
            }

            if (groups.Count == 0)
            {
                groups.Add("all-employees");
            }

            return groups.ToList();
        }
    }
}