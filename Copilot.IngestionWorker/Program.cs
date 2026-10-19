using Copilot.Contracts;
using Copilot.IngestionWorker;
using Copilot.IngestionWorker.Adapters;
using Copilot.IngestionWorker.Services;
using MassTransit;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IMockSharePointAdapter, MockSharePointAdapter>();

builder.Services.AddMassTransit(x =>
{
    x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));

    x.AddRider(rider =>
    {
        rider.AddProducer<NewFileDataEvent>("new-file-data");

        rider.UsingKafka((context, k) =>
        {
            k.Host("localhost:9092");
        });
    });
});

builder.Services.AddHostedService<DocumentIngestionWorker>();
var host = builder.Build();
await host.RunAsync();