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
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHostApplicationLifetime _applicationLifetime;
        private readonly ILogger<DocumentIngestionWorker> _logger;

        private readonly HashSet<string> _processedIds = new();

        public DocumentIngestionWorker(
            IServiceScopeFactory scopeFactory,
            IHostApplicationLifetime applicationLifetime,
            ILogger<DocumentIngestionWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _applicationLifetime = applicationLifetime;
            _logger = logger;
        }

        public async Task ProcessDocumentsOnceAsync(IServiceProvider services, CancellationToken ct)
        {
            try
            {
                var adapter = services.GetRequiredService<IMockSharePointAdapter>();

                var producer = services.GetRequiredService<ITopicProducer<DocumentUpsertEvent>>();

                var orchestrator = services.GetRequiredService<DocumentParsingOrchestrator>();

                var documents = await adapter.GetDocumentsAsync(ct);

                var currentSyncId = Guid.NewGuid().ToString();

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

                        var newEvent = new DocumentUpsertEvent
                        {
                            SyncId = currentSyncId,
                            DocumentId = doc.Id,
                            Action = "create",
                            SourceSystem = "sharepoint",
                            Title = doc.Title,
                            Url = doc.SourceUrl,
                            RawContent = parseResult.ParsedText,
                            LastUpdated = doc.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),

                            Acl = new AclPayload
                            {
                                AllowedGroups = new List<string> { "all-employees" }
                            }
                        };

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

            await WaitForApplicationStartedAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker iteration started at: {Time}", DateTimeOffset.Now);

                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();

                    var services = scope.ServiceProvider;

                    var userAdapter =
                        services.GetRequiredService<GraphUserAdapter>();

                    _logger.LogInformation("Синхронізація юзерів...");

                    await userAdapter.SyncUsersAndGroupsAsync(stoppingToken);

                    _logger.LogInformation("Синхронізація документів...");

                    await ProcessDocumentsOnceAsync(
                        services,
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Помилка під час синхронізації.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task WaitForApplicationStartedAsync(
            CancellationToken ct)
        {
            if (_applicationLifetime.ApplicationStarted.IsCancellationRequested)
                return;

            var tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using var registration =
                _applicationLifetime.ApplicationStarted.Register(
                    () => tcs.TrySetResult(true));

            await tcs.Task.WaitAsync(ct);
        }
    }
}
