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

                IEnumerable<Copilot.IngestionWorker.Models.MockDocument> documents =
                    Array.Empty<Copilot.IngestionWorker.Models.MockDocument>();

                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        documents = await adapter.GetDocumentsAsync(stoppingToken);
                        break;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (attempt < 3)
                    {
                        logger.LogWarning(
                            ex,
                            "Синхронізація не вдалася (спроба {Attempt}/3). Повтор через 5 секунд.",
                            attempt);

                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Синхронізація не вдалася після 3 спроб. Повторимо в наступному циклі.");
                    }
                }

                if (documents.Count() == 0)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                    continue;
                }

                foreach (var doc in documents)
                {
                    if (doc.IsDeleted)
                    {
                        logger.LogInformation(
                            "Обробка видалення документа {DocId}.",
                            doc.Id);
                    }

                    try
                    {
                        logger.LogInformation("Відправка документа {DocId} у Kafka...", doc.Id);
                        
                        var message = new NewFileDataEvent(
                            DocumentId: doc.Id,
                            SourceSystem: "sharepoint",
                            Title: doc.Title,
                            Url: doc.SourceUrl,
                            RawContent: doc.Content,
                            LastUpdated: doc.LastModifiedAt,
                            Acl: new AclRules(doc.AllowedGroups),
                            IsDeleted: doc.IsDeleted
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
