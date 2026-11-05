using Copilot.Contracts;
using MassTransit;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Adapters
{
    public class GraphUserAdapter
    {
        private readonly GraphServiceClient _graphClient;
        private readonly ITopicProducer<KafkaUserAclUpsert> _kafkaProducer;

        public GraphUserAdapter(ITopicProducer<KafkaUserAclUpsert> kafkaProducer)
        {
            _kafkaProducer = kafkaProducer;

            var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider());
            var adapter = new HttpClientRequestAdapter(auth) { BaseUrl = "http://mock-graph:8080/v1.0" };
            _graphClient = new GraphServiceClient(adapter);
        }

        public async Task SyncUsersAndGroupsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var users = await _graphClient.Users.GetAsync(cancellationToken: cancellationToken);

                if (users?.Value == null) return;

                foreach (var user in users.Value)
                {
                    var email = user.Mail ?? user.UserPrincipalName;
                    if (string.IsNullOrEmpty(email)) continue;

                    var userGroups = new List<string> { "all-employees", email };

                    var aclEvent = new KafkaUserAclUpsert
                    {
                        Email = email,
                        LastUpdated = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        Acl = new AclPayload
                        {
                            AllowedGroups = userGroups
                        }
                    };

                    await _kafkaProducer.Produce(aclEvent, cancellationToken);

                    Console.WriteLine($"Відправлено ACL для юзера {email} в Kafka");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Помилка синхронізації юзерів Graph API: {ex.Message}");
            }
        }
    }
}
