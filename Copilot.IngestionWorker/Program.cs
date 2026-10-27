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
       rider.AddProducer<NewFileDataEvent>("new-file-data");
    
      rider.UsingKafka((context, k) =>
      {
           k.Host("kafka:29092");
       });
    });
});

builder.Services.AddHostedService<DocumentIngestionWorker>();
var host = builder.Build();
await host.RunAsync();