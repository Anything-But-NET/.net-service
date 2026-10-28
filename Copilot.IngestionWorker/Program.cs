using Copilot.Contracts;
using Copilot.IngestionWorker;
using Copilot.IngestionWorker.Adapters;
using Copilot.IngestionWorker.Services;
using Copilot.IngestionWorker.Services.Parsing;
using Copilot.IngestionWorker.Services.Parsing.Parsers;
using MassTransit;

var builder = Host.CreateApplicationBuilder(args);


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

builder.Services.AddHostedService<DocumentIngestionWorker>();
var host = builder.Build();
await host.RunAsync();