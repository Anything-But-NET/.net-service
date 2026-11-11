using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Copilot.Contracts;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;

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

        var baseUrl = configuration?["GraphApi:BaseUrl"] ?? "http://mock-graph:8080/v1.0";

        var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider());
        var adapter = new HttpClientRequestAdapter(auth) { BaseUrl = baseUrl };
        _graphClient = new GraphServiceClient(adapter);
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

                var userGroups = new List<string> { "all-employees", email };

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
                _logger.LogInformation("An updated ACL for user {Email} has been sent to Kafka.", email);
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

    private static string ComputeSha256(string rawData)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes);
    }
}