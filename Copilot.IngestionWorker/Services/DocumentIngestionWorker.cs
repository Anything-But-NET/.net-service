using Copilot.Contracts;
using Copilot.IngestionWorker.Adapters;
using Copilot.IngestionWorker.Services.Parsing;
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
        private readonly HashSet<string> _processedIds = new();

        public DocumentIngestionWorker(IServiceProvider serviceProvider, ILogger<DocumentIngestionWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public async Task ProcessDocumentsOnceAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var adapter = scope.ServiceProvider.GetRequiredService<IMockSharePointAdapter>();

                var producer = scope.ServiceProvider.GetRequiredService<ITopicProducer<NewFileDataEvent>>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<DocumentParsingOrchestrator>();

                var documents = await adapter.GetDocumentsAsync(ct);

                if (documents == null)
                {
                    _logger.LogWarning("Adapter returned null documents list.");
                    return;
                }

                foreach (var doc in documents)
                {
                    if (string.IsNullOrEmpty(doc.Id))
                    {
                        _logger.LogWarning("Received document with empty ID. Skipping.");
                        continue;
                    }

                    if (_processedIds.Contains(doc.Id))
                    {
                        _logger.LogDebug("Document {DocId} has already been processed. Skipping.", doc.Id);
                        continue;
                    }

                    try
                    {
                        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(doc.Content ?? string.Empty));

                        var parseResult = await orchestrator.ProcessDocumentAsync(
                            stream,
                            doc.Id,
                            "text/plain",
                            $"{doc.Title ?? "document"}.txt", 
                            ct);

                        if (!parseResult.IsSuccess)
                        {
                            _logger.LogWarning("Failed to parse document {DocId}: {Error}", doc.Id, parseResult.ErrorMessage);
                            continue;
                        }

                        var newEvent = new NewFileDataEvent(
                            DocumentId: doc.Id,
                            SourceSystem: "sharepoint",
                            Title: doc.Title,
                            Url: doc.SourceUrl,
                            RawContent: parseResult.ParsedText,
                            LastUpdated: doc.CreatedAt,
                            Acl: new AclRules(Array.Empty<string>())
                        );

                        await producer.Produce(newEvent, ct);

                        _processedIds.Add(doc.Id);
                        _logger.LogInformation("Document {DocId} successfully published to Kafka.", doc.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process and publish document {DocId}.", doc.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while fetching documents from the adapter.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Document ingestion worker started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker iteration started at: {Time}", DateTimeOffset.Now);

                await ProcessDocumentsOnceAsync(stoppingToken);

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
}
