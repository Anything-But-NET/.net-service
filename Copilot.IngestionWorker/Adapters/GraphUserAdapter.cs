using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Copilot.Contracts;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace Copilot.IngestionWorker.Adapters;

public class GraphUserAdapter
{
    private readonly GraphServiceClient _graphClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<GraphUserAdapter> _logger;
    private readonly ConcurrentDictionary<string, string> _userAclHashes = new(StringComparer.OrdinalIgnoreCase);

    public GraphUserAdapter(
        IServiceProvider serviceProvider,
        ILogger<GraphUserAdapter> logger,
        GraphServiceClient? graphClient = null,
        IConfiguration? configuration = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _graphClient = graphClient ?? GraphClientFactory.Create(configuration);
    }

    public async Task SyncUsersAndGroupsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var allUsers = await FetchAllUsersAsync(cancellationToken);
            if (allUsers.Count == 0)
            {
                _logger.LogWarning("Graph API returned no users.");
                return;
            }

            await using var scope = _serviceProvider.CreateAsyncScope();
            var kafkaProducer = scope.ServiceProvider.GetRequiredService<ITopicProducer<KafkaUserAclUpsert>>();

            int publishedCount = 0;
            int skippedCount = 0;

            foreach (var user in allUsers)
            {
                var email = user.Mail ?? user.UserPrincipalName;
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(user.Id))
                    continue;

                var userGroups = await TryGetUserGroupsAsync(user.Id, email, cancellationToken);
                if (userGroups == null)
                {
                    _logger.LogWarning("Skipping ACL update for user {Email} due to group resolution failure.", email);
                    continue;
                }

                var normalizedGroups = string.Join("|", userGroups.OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
                var aclHash = ComputeSha256($"{email.ToLowerInvariant()}:{normalizedGroups}");

                if (_userAclHashes.TryGetValue(email, out var existingHash) && existingHash == aclHash)
                {
                    skippedCount++;
                    continue;
                }

                var aclEvent = new KafkaUserAclUpsert
                {
                    Email = email,
                    LastUpdated = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    Acl = new AclPayload
                    {
                        AllowedGroups = userGroups
                    }
                };

                await kafkaProducer.Produce(aclEvent, cancellationToken);

                _userAclHashes[email] = aclHash;
                publishedCount++;
                _logger.LogInformation(
                    "An updated ACL for user {Email} (groups: {Groups}) has been sent to Kafka.",
                    email,
                    string.Join(", ", userGroups));
            }

            _logger.LogInformation(
                "ACL synchronization complete: {Published} sent, {Skipped} skipped (no changes).",
                publishedCount,
                skippedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Graph API user synchronization error.");
        }
    }

    private async Task<List<User>> FetchAllUsersAsync(CancellationToken ct)
    {
        var result = new List<User>();
        var page = await _graphClient.Users.GetAsync(cancellationToken: ct);

        while (page != null)
        {
            if (page.Value != null)
            {
                result.AddRange(page.Value);
            }

            if (string.IsNullOrWhiteSpace(page.OdataNextLink))
                break;

            page = await _graphClient.Users.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
        }

        return result;
    }

    private async Task<List<string>?> TryGetUserGroupsAsync(string userId, string email, CancellationToken ct)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var page = await _graphClient.Users[userId].TransitiveMemberOf.GetAsync(cancellationToken: ct);

            while (page != null)
            {
                if (page.Value != null)
                {
                    foreach (var directoryObj in page.Value)
                    {
                        if (directoryObj is DirectoryRole)
                            continue;

                        if (directoryObj is Group group)
                        {
                            if (!string.IsNullOrWhiteSpace(group.DisplayName))
                                groups.Add(group.DisplayName);
                            else if (!string.IsNullOrWhiteSpace(group.Id))
                                groups.Add(group.Id);
                        }
                        else if (directoryObj.AdditionalData != null &&
                                 directoryObj.AdditionalData.TryGetValue("displayName", out var dnObj) &&
                                 dnObj?.ToString() is { Length: > 0 } displayName)
                        {
                            groups.Add(displayName);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(page.OdataNextLink))
                    break;

                page = await _graphClient.Users[userId].TransitiveMemberOf
                    .WithUrl(page.OdataNextLink)
                    .GetAsync(cancellationToken: ct);
            }

            return groups.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch transitiveMemberOf for user {Email} ({UserId}).", email, userId);
            return null;
        }
    }

    private static string ComputeSha256(string rawData)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes);
    }
}