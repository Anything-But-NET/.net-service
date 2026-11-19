using System.Collections.Concurrent;
using Copilot.IngestionWorker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Copilot.IngestionWorker.Adapters
{
    public class MockSharePointAdapter : IMockSharePointAdapter
    {
        private readonly GraphServiceClient _graphClient;
        private readonly ILogger<MockSharePointAdapter> _logger;
        private readonly ConcurrentDictionary<string, string> _driveDeltaLinks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _itemVersionCache = new(StringComparer.OrdinalIgnoreCase);

        public MockSharePointAdapter(
            GraphServiceClient? graphClient = null,
            IConfiguration? configuration = null,
            ILogger<MockSharePointAdapter>? logger = null)
        {
            _graphClient = graphClient ?? GraphClientFactory.Create(configuration);
            _logger = logger ?? NullLogger<MockSharePointAdapter>.Instance;
        }

        public async Task<IEnumerable<MockDocument>> GetDocumentsAsync(CancellationToken cancellationToken)
        {
            var mockDocuments = new List<MockDocument>();

            try
            {
                var sitesList = await FetchAllSitesAsync(cancellationToken);
                _logger.LogInformation("Found {SiteCount} SharePoint site(s).", sitesList.Count);

                foreach (var site in sitesList)
                {
                    if (string.IsNullOrWhiteSpace(site.Id))
                        continue;

                    var sitePrimaryGroupName = !string.IsNullOrWhiteSpace(site.DisplayName)
                        ? site.DisplayName.Trim()
                        : site.Name?.Trim();

                    var drives = await FetchAllDrivesAsync(site.Id, cancellationToken);

                    foreach (var drive in drives)
                    {
                        if (string.IsNullOrWhiteSpace(drive.Id))
                            continue;

                        _logger.LogInformation(
                            "Scanning SharePoint site '{SiteName}', drive '{DriveName}' ({DriveId})...",
                            sitePrimaryGroupName ?? site.Id,
                            drive.Name ?? drive.Id,
                            drive.Id);

                        var deltaItems = await FetchDeltaItemsAsync(drive.Id, cancellationToken);

                        foreach (var item in deltaItems)
                        {
                            if (item.File == null || item.Deleted != null || string.IsNullOrWhiteSpace(item.Id))
                                continue;

                            var versionToken = item.ETag
                                ?? item.CTag
                                ?? item.LastModifiedDateTime?.UtcDateTime.ToString("O")
                                ?? string.Empty;

                            var cacheKey = $"{drive.Id}:{item.Id}";
                            if (!string.IsNullOrEmpty(versionToken) &&
                                _itemVersionCache.TryGetValue(cacheKey, out var cachedVersion) &&
                                cachedVersion == versionToken)
                            {
                                _logger.LogDebug("Skipping download for unchanged item {ItemId} ({Name}).", item.Id, item.Name);
                                continue;
                            }

                            var allowedGroups = await TryResolveDocumentGroupsAsync(
                                drive.Id,
                                item,
                                sitePrimaryGroupName,
                                cancellationToken);

                            if (allowedGroups == null || allowedGroups.Count == 0)
                            {
                                _logger.LogWarning(
                                    "Skipping document {ItemId} ('{Name}') because permissions could not be safely resolved.",
                                    item.Id,
                                    item.Name);
                                continue;
                            }

                            using var contentStream = await _graphClient.Drives[drive.Id].Items[item.Id].Content
                                .GetAsync(cancellationToken: cancellationToken);
                            if (contentStream == null)
                                continue;

                            using var ms = new MemoryStream();
                            await contentStream.CopyToAsync(ms, cancellationToken);
                            var rawBytes = ms.ToArray();

                            mockDocuments.Add(new MockDocument(
                                Id: item.Id,
                                Title: item.Name ?? "Untitled",
                                Content: string.Empty,
                                SourceUrl: item.WebUrl ?? "",
                                CreatedAt: item.LastModifiedDateTime?.UtcDateTime
                                           ?? item.CreatedDateTime?.UtcDateTime
                                           ?? DateTime.UtcNow,
                                AllowedGroups: allowedGroups,
                                RawBytes: rawBytes,
                                ContentType: item.File.MimeType
                            ));

                            if (!string.IsNullOrEmpty(versionToken))
                            {
                                _itemVersionCache[cacheKey] = versionToken;
                            }
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

        private async Task<List<Site>> FetchAllSitesAsync(CancellationToken ct)
        {
            var sitesById = new Dictionary<string, Site>(StringComparer.OrdinalIgnoreCase);

            var page = await _graphClient.Sites.GetAsync(cancellationToken: ct);
            while (page != null)
            {
                if (page.Value != null)
                {
                    foreach (var s in page.Value)
                    {
                        if (!string.IsNullOrWhiteSpace(s.Id))
                            sitesById.TryAdd(s.Id, s);
                    }
                }

                if (string.IsNullOrWhiteSpace(page.OdataNextLink))
                    break;

                page = await _graphClient.Sites.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
            }

            try
            {
                var searchPage = await _graphClient.Sites.GetAsync(config =>
                {
                    config.QueryParameters.Search = "*";
                }, ct);

                while (searchPage != null)
                {
                    if (searchPage.Value != null)
                    {
                        foreach (var s in searchPage.Value)
                        {
                            if (!string.IsNullOrWhiteSpace(s.Id))
                                sitesById.TryAdd(s.Id, s);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(searchPage.OdataNextLink))
                        break;

                    searchPage = await _graphClient.Sites.WithUrl(searchPage.OdataNextLink).GetAsync(cancellationToken: ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sites search=* not supported by this Graph endpoint, using default sites list.");
            }

            return sitesById.Values.ToList();
        }

        private async Task<List<Drive>> FetchAllDrivesAsync(string siteId, CancellationToken ct)
        {
            var drives = new List<Drive>();
            var page = await _graphClient.Sites[siteId].Drives.GetAsync(cancellationToken: ct);

            while (page != null)
            {
                if (page.Value != null)
                {
                    drives.AddRange(page.Value);
                }

                if (string.IsNullOrWhiteSpace(page.OdataNextLink))
                    break;

                page = await _graphClient.Sites[siteId].Drives.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
            }

            return drives;
        }

        private async Task<List<DriveItem>> FetchDeltaItemsAsync(string driveId, CancellationToken ct)
        {
            var items = new List<DriveItem>();
            var deltaBuilder = _graphClient.Drives[driveId].Items["root"].Delta;

            var page = _driveDeltaLinks.TryGetValue(driveId, out var savedDeltaLink) && !string.IsNullOrWhiteSpace(savedDeltaLink)
                ? await deltaBuilder.WithUrl(savedDeltaLink).GetAsDeltaGetResponseAsync(cancellationToken: ct)
                : await deltaBuilder.GetAsDeltaGetResponseAsync(cancellationToken: ct);

            while (page != null)
            {
                if (page.Value != null)
                {
                    items.AddRange(page.Value);
                }

                if (!string.IsNullOrWhiteSpace(page.OdataNextLink))
                {
                    page = await deltaBuilder.WithUrl(page.OdataNextLink).GetAsDeltaGetResponseAsync(cancellationToken: ct);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(page.OdataDeltaLink))
                {
                    _driveDeltaLinks[driveId] = page.OdataDeltaLink;
                }

                break;
            }

            return items;
        }

        private async Task<IReadOnlyList<string>?> TryResolveDocumentGroupsAsync(
            string driveId,
            DriveItem item,
            string? sitePrimaryGroupName,
            CancellationToken ct)
        {
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var page = await _graphClient.Drives[driveId].Items[item.Id!].Permissions
                    .GetAsync(cancellationToken: ct);

                while (page != null)
                {
                    if (page.Value != null)
                    {
                        foreach (var perm in page.Value)
                        {
                            ExtractPermissionGroups(perm, sitePrimaryGroupName, groups);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(page.OdataNextLink))
                        break;

                    page = await _graphClient.Drives[driveId].Items[item.Id!].Permissions
                        .WithUrl(page.OdataNextLink)
                        .GetAsync(cancellationToken: ct);
                }

                return groups.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch permissions for item {ItemId} on drive {DriveId}.", item.Id, driveId);
                return null;
            }
        }

        private static void ExtractPermissionGroups(
            Permission perm,
            string? sitePrimaryGroupName,
            HashSet<string> groups)
        {
            bool isMemberSitePermission = perm.Roles != null &&
                perm.Roles.Any(r => string.Equals(r, "write", StringComparison.OrdinalIgnoreCase));

            void AddIdentity(SharePointIdentitySet? spIdentity, IdentitySet? baseIdentity)
            {
                var entraGroupName = spIdentity?.Group?.DisplayName;
                if (!string.IsNullOrWhiteSpace(entraGroupName))
                {
                    groups.Add(entraGroupName.Trim());
                }

                var siteGroup = spIdentity?.SiteGroup;
                if (siteGroup != null)
                {
                    if (!string.IsNullOrWhiteSpace(siteGroup.DisplayName))
                    {
                        groups.Add(siteGroup.DisplayName.Trim());
                    }

                    if (isMemberSitePermission && !string.IsNullOrWhiteSpace(sitePrimaryGroupName))
                    {
                        groups.Add(sitePrimaryGroupName);
                    }
                }

                var userName = spIdentity?.User?.DisplayName ?? baseIdentity?.User?.DisplayName;
                if (!string.IsNullOrWhiteSpace(userName))
                {
                    groups.Add(userName.Trim());
                }
            }

            AddIdentity(perm.GrantedToV2, perm.GrantedTo);

            if (perm.GrantedToIdentitiesV2 != null)
            {
                foreach (var identity in perm.GrantedToIdentitiesV2)
                {
                    AddIdentity(identity, null);
                }
            }

            if (perm.GrantedToIdentities != null)
            {
                foreach (var identity in perm.GrantedToIdentities)
                {
                    AddIdentity(null, identity);
                }
            }
        }
    }
}