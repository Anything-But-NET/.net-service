using Copilot.Contracts;
using Copilot.IngestionWorker.Adapters;
using MassTransit;
using System;
using System.Collections.Generic;
using System.Text;

namespace Copilot.IngestionWorker.Services
{
    public class DocumentIngestionWorker : BackgroundService
    {
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger<DocumentIngestionWorker> logger;

        public DocumentIngestionWorker(
            IServiceProvider serviceProvider,
            ILogger<DocumentIngestionWorker> logger
            )
        {
            this.serviceProvider = serviceProvider;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Початок завантаження документів з Mock SharePoint...");

            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = serviceProvider.CreateScope();
                var adapter = scope.ServiceProvider.GetRequiredService<IMockSharePointAdapter>();

                var producer = scope.ServiceProvider.GetRequiredService<ITopicProducer<NewFileDataEvent>>();

                var documents = await adapter.GetDocumentsAsync(stoppingToken);

                foreach (var doc in documents)
                {
                    try
                    {
                        logger.LogInformation("Відправка документа {DocId} у Kafka...", doc.Id);

                        var message = new NewFileDataEvent(
                            DocumentId: doc.Id,
                            SourceSystem: "sharepoint",
                            Title: doc.Title,
                            Url: doc.SourceUrl,
                            RawContent: doc.Content,
                            LastUpdated: doc.CreatedAt,
                            Acl: new AclRules(Array.Empty<string>())
                        );

                        await producer.Produce(message, stoppingToken);

                        logger.LogInformation("Документ {DocId} успішно відправлено.", doc.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Падіння при відправці документа {DocId}. Йдемо далі.", doc.Id);
                        continue;
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}
