using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Copilot.Contracts;
using Copilot.IngestionWorker.Adapters;
using Copilot.IngestionWorker.Services.Parsing;
using MassTransit;

namespace Copilot.IngestionWorker.Services;

public class DocumentIngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<DocumentIngestionWorker> _logger;
    private readonly IngestionTriggerService _triggerService;

    private readonly ConcurrentDictionary<string, string> _documentHashes = new();

    public DocumentIngestionWorker(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime applicationLifetime,
        ILogger<DocumentIngestionWorker> logger,
        IngestionTriggerService? triggerService = null)
    {
        _scopeFactory = scopeFactory;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _triggerService = triggerService ?? new IngestionTriggerService();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Document ingestion worker started (On-Demand mode).");

        await WaitForApplicationStartedAsync(stoppingToken);

        _triggerService.TriggerSync("Application Startup");

        try
        {
            await foreach (var reason in _triggerService.ReadTriggersAsync(stoppingToken))
            {
                _logger.LogInformation("Worker triggered by: {Reason} at {Time}", reason, DateTimeOffset.UtcNow);

                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var services = scope.ServiceProvider;

                    var userAdapter = services.GetRequiredService<GraphUserAdapter>();

                    _logger.LogInformation("Synchronizing users...");
                    await userAdapter.SyncUsersAndGroupsAsync(stoppingToken);

                    _logger.LogInformation("Synchronizing documents...");
                    await ProcessDocumentsOnceAsync(services, stoppingToken);

                    _logger.LogInformation("Synchronization complete. Waiting for the next trigger...");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during synchronization (Trigger: {Reason}).", reason);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Document ingestion worker is stopping.");
        }
    }

    public async Task ProcessDocumentsOnceAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var adapter = services.GetRequiredService<IMockSharePointAdapter>();
            var producer = services.GetRequiredService<ITopicProducer<DocumentUpsertEvent>>();
            var orchestrator = services.GetRequiredService<DocumentParsingOrchestrator>();

            var documents = await adapter.GetDocumentsAsync(ct);

            if (documents == null)
            {
                _logger.LogWarning("Adapter returned null documents list.");
                return;
            }

            var currentSyncId = Guid.NewGuid().ToString();
            int publishedCount = 0;
            int skippedCount = 0;

            foreach (var doc in documents)
            {
                if (string.IsNullOrEmpty(doc.Id))
                {
                    _logger.LogWarning("Received document with empty ID. Skipping.");
                    continue;
                }

                var contentHash = ComputeSha256($"{doc.Title}|{doc.SourceUrl}|{doc.Content}");

                if (_documentHashes.TryGetValue(doc.Id, out var existingHash) && existingHash == contentHash)
                {
                    _logger.LogDebug("Document {DocId} is unchanged. Skipping.", doc.Id);
                    skippedCount++;
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
                        Action = existingHash == null ? "create" : "update",
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

                    _documentHashes[doc.Id] = contentHash;
                    publishedCount++;
                    _logger.LogInformation("Document {DocId} ({Action}) published to Kafka.", doc.Id, newEvent.Action);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process and publish document {DocId}.", doc.Id);
                }
            }

            _logger.LogInformation("Documents sync finished: {Published} published, {Skipped} unchanged skipped.", publishedCount, skippedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while fetching documents from the adapter.");
        }
    }

    private static string ComputeSha256(string rawData)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes);
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken ct)
    {
        if (_applicationLifetime.ApplicationStarted.IsCancellationRequested)
            return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = _applicationLifetime.ApplicationStarted.Register(() => tcs.TrySetResult(true));
        await tcs.Task.WaitAsync(ct);
    }
}