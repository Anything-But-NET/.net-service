using Copilot.Contracts;
using Copilot.IngestionWorker.Adapters;
using Copilot.IngestionWorker.Services;
using Copilot.IngestionWorker.Services.Parsing;
using Copilot.IngestionWorker.Services.Parsing.Parsers;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

builder.Services.AddSingleton<IngestionTriggerService>();

builder.Services.AddSingleton<IMockSharePointAdapter, MockSharePointAdapter>();
builder.Services.AddTransient<GraphUserAdapter>();
builder.Services.AddTransient<IDocumentParser, TextDocumentParser>();
builder.Services.AddTransient<IDocumentParser, DocxMarkdownParser>();
builder.Services.AddTransient<IDocumentParser, PdfMarkdownParser>();
builder.Services.AddTransient<DocumentParsingOrchestrator>();

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services.Configure<MassTransitHostOptions>(options =>
{
    options.WaitUntilStarted = true;
    options.StartTimeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddMassTransit(x =>
{
    x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));

    x.AddRider(rider =>
    {
        rider.AddProducer<DocumentUpsertEvent>("rag.documents.ingestion.v1");
        rider.AddProducer<KafkaUserAclUpsert>("rag.users.acl.v1");

        rider.UsingKafka((context, k) =>
        {
            var kafkaHost = builder.Configuration["Kafka:Host"] ?? "kafka:9094";
            k.Host(kafkaHost);
        });
    });
});

builder.Services.AddHostedService<DocumentIngestionWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();
app.MapControllers();

app.MapPost("/api/ingestion/trigger", (IngestionTriggerService triggerService, string? reason) =>
{
    var triggerReason = string.IsNullOrWhiteSpace(reason) ? "Manual API Trigger" : reason;
    var isQueued = triggerService.TriggerSync(triggerReason);

    return isQueued
        ? Results.Accepted(value: new { status = "Queued", reason = triggerReason })
        : Results.Conflict(new { status = "AlreadyRunningOrQueued", message = "Sync is already in the queue." });
});

app.MapGet("/api/documents/{id}", (string id, HttpContext context) =>
{
    var userId = context.Request.Headers["X-User-Id"].ToString();

    var document = new
    {
        Id = "Doc",
        Content = "Top secret",
        AllowedUsers = new[] { "UserA" },
    };

    if (id != document.Id)
    {
        return Results.NotFound("Document not found!");
    }

    if (!document.AllowedUsers.Contains(userId))
    {
        return Results.Problem("Access denied", statusCode: 403);
    }

    return Results.Ok(new { document.Id, document.Content });
});

app.Run();