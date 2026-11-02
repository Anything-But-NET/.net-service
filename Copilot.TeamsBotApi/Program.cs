using Copilot.Contracts;
using Copilot.IngestionWorker.Adapters;
using Copilot.IngestionWorker.Services.Parsing;
using Copilot.IngestionWorker.Services.Parsing.Parsers;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IMockSharePointAdapter, MockSharePointAdapter>();
builder.Services.AddTransient<IDocumentParser, TextDocumentParser>();
builder.Services.AddTransient<IDocumentParser, DocxMarkdownParser>();
builder.Services.AddTransient<IDocumentParser, PdfMarkdownParser>();
builder.Services.AddTransient<DocumentParsingOrchestrator>();

builder.Services.AddMassTransit(x =>
{
    x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));

    x.AddRider(rider =>
    {
        rider.AddProducer<DocumentSyncEvent>("rag.documents.ingestion.v1");

        rider.UsingKafka((context, k) =>
        {
            k.Host("kafka:9094");
        });
    });
});

builder.Services.AddHostedService<Copilot.IngestionWorker.Services.DocumentIngestionWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapGet("/api/documents/{id}", (string id, HttpContext context) =>
{
    var userId = context.Request.Headers["X-User-Id"].ToString();

    var document = new
    {
        Id = "Doc",
        Content = "Top secret",
        AllowedUsers = new[] { "UserA" },
    };

    if(id != document.Id)
    {
        return Results.NotFound("Document not found!");
    }

    if(!document.AllowedUsers.Contains(userId))
    {
        return Results.Problem("Access denied", statusCode: 403);
    }

    return Results.Ok(new { document.Id, document.Content });
});

app.Run();
