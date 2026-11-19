using System.Collections.Concurrent;
using Copilot.IngestionWorker.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Copilot.IngestionWorker.Adapters
{
    public class MockSharePointAdapter : IMockSharePointAdapter
    {
        private sealed record CachedItemState(string VersionToken, string AclKey, byte[] RawBytes, string? ContentType);
        private sealed record PendingDriveCheckpoint(string? DeltaLink, HashSet<string> DocumentIds, bool HadAdapterFailure);

        private readonly GraphServiceClient _graphClient;
        private readonly ILogger<MockSharePointAdapter> _logger;
        private readonly ConcurrentDictionary<string, string> _driveDeltaLinks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CachedItemState> _itemStateCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _userEmailCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _groupDisplayNameCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _groupOwnerEmailsCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, PendingDriveCheckpoint> _pendingDriveCheckpoints = new(StringComparer.OrdinalIgnoreCase);

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
            _pendingDriveCheckpoints.Clear();

            List<Site> sitesList;
            try
            {
                sitesList = await FetchAllSitesAsync(cancellationToken);
                _logger.LogInformation("Found {SiteCount} SharePoint site(s).", sitesList.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch SharePoint sites list.");
                return mockDocuments;
            }

            foreach (var site in sitesList)
            {
                if (string.IsNullOrWhiteSpace(site.Id))
                    continue;

                var siteName = !string.IsNullOrWhiteSpace(site.DisplayName)
                    ? site.DisplayName.Trim()
                    : site.Name?.Trim();

                List<Drive> drives;
                try
                {
                    drives = await FetchAllDrivesAsync(site.Id, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to list drives for SharePoint site '{SiteName}' ({SiteId}). Skipping site.",
                        siteName ?? site.Id, site.Id);
                    continue;
                }

                foreach (var drive in drives)
                {
                    if (string.IsNullOrWhiteSpace(drive.Id))
                        continue;

                    var driveOwnerGroupId = ExtractOwnerGroupId(drive);

                    _logger.LogInformation(
                        "Scanning SharePoint site '{SiteName}', drive '{DriveName}' ({DriveId})...",
                        siteName ?? site.Id,
                        drive.Name ?? drive.Id,
                        drive.Id);

                    List<DriveItem> deltaItems;
                    string? pendingDeltaLink;
                    try
                    {
                        (deltaItems, pendingDeltaLink) = await FetchDeltaItemsAsync(drive.Id, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch delta for drive {DriveId} on site '{SiteName}'. Skipping drive.",
                            drive.Id, siteName ?? site.Id);
                        continue;
                    }

                    var driveDocIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    bool driveHadAdapterFailure = false;

                    foreach (var item in deltaItems)
                    {
                        if (item.File == null || item.Deleted != null || string.IsNullOrWhiteSpace(item.Id))
                            continue;

                        try
                        {
                            var allowedGroups = await TryResolveDocumentGroupsAsync(
                                drive.Id,
                                item,
                                driveOwnerGroupId,
                                cancellationToken);

                            if (allowedGroups == null || allowedGroups.Count == 0)
                            {
                                _logger.LogWarning(
                                    "Skipping document {ItemId} ('{Name}') because permissions could not be safely resolved.",
                                    item.Id,
                                    item.Name);
                                driveHadAdapterFailure = true;
                                continue;
                            }

                            var versionToken = item.ETag
                                ?? item.CTag
                                ?? item.LastModifiedDateTime?.UtcDateTime.ToString("O")
                                ?? string.Empty;

                            var aclKey = string.Join("|", allowedGroups.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
                            var cacheKey = $"{drive.Id}:{item.Id}";

                            _itemStateCache.TryGetValue(cacheKey, out var cachedState);

                            bool versionUnchanged = !string.IsNullOrEmpty(versionToken) &&
                                                    cachedState != null &&
                                                    cachedState.VersionToken == versionToken;

                            if (versionUnchanged && cachedState!.AclKey == aclKey)
                            {
                                _logger.LogDebug("Skipping unchanged item {ItemId} ({Name}).", item.Id, item.Name);
                                continue;
                            }

                            byte[] rawBytes;
                            string? contentType;

                            if (versionUnchanged && cachedState != null)
                            {
                                rawBytes = cachedState.RawBytes;
                                contentType = item.File.MimeType ?? cachedState.ContentType;
                            }
                            else
                            {
                                using var contentStream = await _graphClient.Drives[drive.Id].Items[item.Id].Content
                                    .GetAsync(cancellationToken: cancellationToken);
                                if (contentStream == null)
                                {
                                    driveHadAdapterFailure = true;
                                    continue;
                                }

                                using var ms = new MemoryStream();
                                await contentStream.CopyToAsync(ms, cancellationToken);
                                rawBytes = ms.ToArray();
                                contentType = item.File.MimeType;
                            }

                            driveDocIds.Add(item.Id);

                            var capturedBytes = rawBytes;
                            var capturedContentType = contentType;
                            mockDocuments.Add(new MockDocument(
                                Id: item.Id,
                                Title: item.Name ?? "Untitled",
                                Content: string.Empty,
                                SourceUrl: item.WebUrl ?? "",
                                CreatedAt: item.LastModifiedDateTime?.UtcDateTime
                                           ?? item.CreatedDateTime?.UtcDateTime
                                           ?? DateTime.UtcNow,
                                AllowedGroups: allowedGroups,
                                RawBytes: capturedBytes,
                                ContentType: capturedContentType,
                                OnSynced: () =>
                                {
                                    if (!string.IsNullOrEmpty(versionToken))
                                    {
                                        _itemStateCache[cacheKey] = new CachedItemState(
                                            versionToken,
                                            aclKey,
                                            capturedBytes,
                                            capturedContentType);
                                    }
                                }
                            ));
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            driveHadAdapterFailure = true;
                            _logger.LogError(ex, "Failed to download/resolve item {ItemId} ('{Name}') on drive {DriveId}.",
                                item.Id, item.Name, drive.Id);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(pendingDeltaLink))
                    {
                        _pendingDriveCheckpoints[drive.Id] = new PendingDriveCheckpoint(
                            pendingDeltaLink,
                            driveDocIds,
                            driveHadAdapterFailure);
                    }
                }
            }

            return mockDocuments;
        }

        public void CompleteSync(IReadOnlySet<string> failedDocumentIds)
        {
            foreach (var (driveId, checkpoint) in _pendingDriveCheckpoints)
            {
                if (checkpoint.HadAdapterFailure)
                    continue;

                if (checkpoint.DocumentIds.Any(id => failedDocumentIds.Contains(id)))
                    continue;

                if (!string.IsNullOrWhiteSpace(checkpoint.DeltaLink))
                {
                    _driveDeltaLinks[driveId] = checkpoint.DeltaLink;
                }
            }

            _pendingDriveCheckpoints.Clear();
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
            var page = await _graphClient.Sites[siteId].Drives.GetAsync(config =>
            {
                config.QueryParameters.Select = ["id", "name", "owner"];
            }, ct);

            while (page != null)
            {
                if (page.Value != null)
                    drives.AddRange(page.Value);

                if (string.IsNullOrWhiteSpace(page.OdataNextLink))
                    break;

                page = await _graphClient.Sites[siteId].Drives.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
            }

            return drives;
        }

        private static string? ExtractOwnerGroupId(Drive drive)
        {
            if (drive.Owner?.AdditionalData == null ||
                !drive.Owner.AdditionalData.TryGetValue("group", out var groupObj) ||
                groupObj == null)
            {
                return null;
            }

            if (groupObj is UntypedObject untypedObj)
            {
                var props = untypedObj.GetValue();
                if (props != null &&
                    props.TryGetValue("id", out var idNode) &&
                    idNode is UntypedString idStr)
                {
                    var value = idStr.GetValue();
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }

            return null;
        }

        private async Task<(List<DriveItem> Items, string? PendingDeltaLink)> FetchDeltaItemsAsync(string driveId, CancellationToken ct)
        {
            var items = new List<DriveItem>();
            string? pendingDeltaLink = null;
            var deltaBuilder = _graphClient.Drives[driveId].Items["root"].Delta;

            var page = _driveDeltaLinks.TryGetValue(driveId, out var savedDeltaLink) && !string.IsNullOrWhiteSpace(savedDeltaLink)
                ? await deltaBuilder.WithUrl(savedDeltaLink).GetAsDeltaGetResponseAsync(cancellationToken: ct)
                : await deltaBuilder.GetAsDeltaGetResponseAsync(cancellationToken: ct);

            while (page != null)
            {
                if (page.Value != null)
                    items.AddRange(page.Value);

                if (!string.IsNullOrWhiteSpace(page.OdataNextLink))
                {
                    page = await deltaBuilder.WithUrl(page.OdataNextLink).GetAsDeltaGetResponseAsync(cancellationToken: ct);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(page.OdataDeltaLink))
                {
                    pendingDeltaLink = page.OdataDeltaLink;
                }

                break;
            }

            return (items, pendingDeltaLink);
        }

        private async Task<IReadOnlyList<string>?> TryResolveDocumentGroupsAsync(
            string driveId,
            DriveItem item,
            string? driveOwnerGroupId,
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
                            await ExtractPermissionGroupsAsync(driveId, item.Id!, perm, driveOwnerGroupId, groups, ct);
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

        private async Task ExtractPermissionGroupsAsync(
            string driveId,
            string itemId,
            Permission perm,
            string? driveOwnerGroupId,
            HashSet<string> groups,
            CancellationToken ct)
        {
            bool isOwnerRole = perm.Roles != null &&
                perm.Roles.Any(r => string.Equals(r, "owner", StringComparison.OrdinalIgnoreCase));

            bool isWriteRole = !isOwnerRole &&
                perm.Roles != null &&
                perm.Roles.Any(r => string.Equals(r, "write", StringComparison.OrdinalIgnoreCase));

            async Task AddIdentityAsync(SharePointIdentitySet? spIdentity, IdentitySet? baseIdentity)
            {
                var entraGroupName = spIdentity?.Group?.DisplayName;
                if (!string.IsNullOrWhiteSpace(entraGroupName))
                {
                    groups.Add(entraGroupName.Trim());
                }

                var siteGroup = spIdentity?.SiteGroup;
                if (siteGroup != null)
                {
                    if (isOwnerRole && !string.IsNullOrWhiteSpace(driveOwnerGroupId))
                    {
                        var ownerEmails = await ResolveGroupOwnerEmailsAsync(driveOwnerGroupId, ct);
                        foreach (var email in ownerEmails)
                        {
                            groups.Add(email);
                        }
                    }
                    else if (isWriteRole && !string.IsNullOrWhiteSpace(driveOwnerGroupId))
                    {
                        var groupName = await ResolveGroupDisplayNameAsync(driveOwnerGroupId, ct);
                        if (!string.IsNullOrWhiteSpace(groupName))
                        {
                            groups.Add(groupName);
                        }
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Skipping unresolvable SharePoint siteGroup '{SiteGroupName}' on item {ItemId} (drive {DriveId}).",
                            siteGroup.DisplayName ?? siteGroup.Id ?? "unknown",
                            itemId,
                            driveId);
                    }
                }

                var userEmail = await ResolveUserEmailAsync(spIdentity, baseIdentity, ct);
                if (!string.IsNullOrWhiteSpace(userEmail))
                {
                    groups.Add(userEmail);
                }
            }

            await AddIdentityAsync(perm.GrantedToV2, perm.GrantedTo);

            if (perm.GrantedToIdentitiesV2 != null)
            {
                foreach (var identity in perm.GrantedToIdentitiesV2)
                {
                    await AddIdentityAsync(identity, null);
                }
            }

            if (perm.GrantedToIdentities != null)
            {
                foreach (var identity in perm.GrantedToIdentities)
                {
                    await AddIdentityAsync(null, identity);
                }
            }
        }

        private async Task<string?> ResolveGroupDisplayNameAsync(string groupId, CancellationToken ct)
        {
            if (_groupDisplayNameCache.TryGetValue(groupId, out var cachedName))
                return cachedName;

            var group = await _graphClient.Groups[groupId].GetAsync(config =>
            {
                config.QueryParameters.Select = ["id", "displayName"];
            }, ct);

            var displayName = group?.DisplayName?.Trim();
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                _groupDisplayNameCache[groupId] = displayName;
                return displayName;
            }

            return null;
        }

        private async Task<IReadOnlyList<string>> ResolveGroupOwnerEmailsAsync(string groupId, CancellationToken ct)
        {
            if (_groupOwnerEmailsCache.TryGetValue(groupId, out var cachedEmails))
                return cachedEmails;

            var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var page = await _graphClient.Groups[groupId].Owners.GetAsync(cancellationToken: ct);

            while (page != null)
            {
                if (page.Value != null)
                {
                    foreach (var obj in page.Value)
                    {
                        if (obj is User ownerUser)
                        {
                            var email = ownerUser.Mail ?? ownerUser.UserPrincipalName;
                            if (!string.IsNullOrWhiteSpace(email))
                            {
                                emails.Add(email.Trim());
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(page.OdataNextLink))
                    break;

                page = await _graphClient.Groups[groupId].Owners.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
            }

            var result = emails.ToList();
            _groupOwnerEmailsCache[groupId] = result;
            return result;
        }

        private async Task<string?> ResolveUserEmailAsync(
            SharePointIdentitySet? spIdentity,
            IdentitySet? baseIdentity,
            CancellationToken ct)
        {
            static string? ExtractEmailFromIdentity(Identity? identity)
            {
                if (identity?.AdditionalData != null &&
                    identity.AdditionalData.TryGetValue("email", out var emailObj) &&
                    emailObj?.ToString() is { Length: > 0 } emailStr &&
                    emailStr.Contains('@'))
                {
                    return emailStr.Trim();
                }

                return null;
            }

            var directEmail = ExtractEmailFromIdentity(spIdentity?.User)
                ?? ExtractEmailFromIdentity(spIdentity?.SiteUser)
                ?? ExtractEmailFromIdentity(baseIdentity?.User);

            if (!string.IsNullOrWhiteSpace(directEmail))
                return directEmail;

            var loginName = spIdentity?.SiteUser?.LoginName;
            if (!string.IsNullOrWhiteSpace(loginName))
            {
                var candidate = loginName.Contains('|')
                    ? loginName[(loginName.LastIndexOf('|') + 1)..]
                    : loginName;

                if (candidate.Contains('@'))
                    return candidate.Trim();
            }

            var userId = spIdentity?.User?.Id ?? baseIdentity?.User?.Id;
            if (!string.IsNullOrWhiteSpace(userId) && Guid.TryParse(userId, out _))
            {
                if (_userEmailCache.TryGetValue(userId, out var cachedEmail))
                    return cachedEmail;

                try
                {
                    var user = await _graphClient.Users[userId].GetAsync(cancellationToken: ct);
                    var resolved = user?.Mail ?? user?.UserPrincipalName;
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        _userEmailCache[userId] = resolved.Trim();
                        return resolved.Trim();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not resolve email for user ID {UserId}.", userId);
                }
            }

            return null;
        }
    }
}