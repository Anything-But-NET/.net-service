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
    private static readonly ConcurrentDictionary<string, string> _userAclHashes = new(StringComparer.OrdinalIgnoreCase);

    public GraphUserAdapter(
        IServiceProvider serviceProvider,
        ILogger<GraphUserAdapter> logger,
        IConfiguration? configuration = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _graphClient = GraphClientFactory.Create(configuration);
    }

    public async Task SyncUsersAndGroupsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var users = await _graphClient.Users.GetAsync(cancellationToken: cancellationToken);

            if (users?.Value == null)
            {
                _logger.LogWarning("Graph API returned null users list.");
                return;
            }

            await using var scope = _serviceProvider.CreateAsyncScope();
            var kafkaProducer = scope.ServiceProvider.GetRequiredService<ITopicProducer<KafkaUserAclUpsert>>();

            int publishedCount = 0;
            int skippedCount = 0;

            foreach (var user in users.Value)
            {
                var email = user.Mail ?? user.UserPrincipalName;
                if (string.IsNullOrWhiteSpace(email))
                    continue;

                var userGroups = await GetUserGroupsAsync(user.Id, email, cancellationToken);

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
                _logger.LogInformation("An updated ACL for user {Email} (groups: {Groups}) has been sent to Kafka.", email, string.Join(", ", userGroups));
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

    private async Task<List<string>> GetUserGroupsAsync(string? userId, string email, CancellationToken ct)
    {
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(userId))
        {
            try
            {
                var memberOf = await _graphClient.Users[userId].TransitiveMemberOf.GetAsync(cancellationToken: ct);
                if (memberOf?.Value != null)
                {
                    foreach (var directoryObj in memberOf.Value)
                    {
                        if (directoryObj is DirectoryRole)
                        {
                            continue;
                        }

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
                        else if (!string.IsNullOrWhiteSpace(directoryObj.Id))
                        {
                            groups.Add(directoryObj.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch transitiveMemberOf for user {Email} ({UserId}).", email, userId);
            }
        }

        if (groups.Count == 0)
        {
            groups.Add("all-employees");
        }

        return groups.ToList();
    }

    private static string ComputeSha256(string rawData)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes);
    }
}