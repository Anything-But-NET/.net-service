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
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<DocumentIngestionWorker> _logger;
        private readonly ITopicProducer<NewFileDataEvent> _producer;

        public DocumentIngestionWorker(
            IServiceProvider serviceProvider,
            ILogger<DocumentIngestionWorker> logger,
            ITopicProducer<NewFileDataEvent> producer)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _producer = producer;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Початок завантаження документів з Mock SharePoint...");

            while (!stoppingToken.IsCancellationRequested)
            {
                using var scope = _serviceProvider.CreateScope();
                var adapter = scope.ServiceProvider.GetRequiredService<IMockSharePointAdapter>();

                var documents = await adapter.GetDocumentsAsync(stoppingToken);

                foreach (var doc in documents)
                {
                    try
                    {
                        _logger.LogInformation("Відправка документа {DocId} у Kafka...", doc.Id);

                        var message = new NewFileDataEvent(
                            DocumentId: doc.Id,
                            SourceSystem: "sharepoint",
                            Title: doc.Title,
                            Url: doc.SourceUrl,
                            RawContent: doc.Content,
                            LastUpdated: doc.CreatedAt,
                            Acl: new AclRules(Array.Empty<string>())
                        );

                        await _producer.Produce(message, stoppingToken);

                        _logger.LogInformation("Документ {DocId} успішно відправлено.", doc.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Падіння при відправці документа {DocId}. Йдемо далі.", doc.Id);
                        continue;
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }
}
