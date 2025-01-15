using Microsoft.AspNetCore.Mvc;
using CommandLine;

static class Microservice
{

    public static void Run(IConfiguration configuration, Database database, TranskribusClient transkribusClient, MsvcOptions options)
    {
        Console.WriteLine("Setting up microservice...");
        var builder = WebApplication.CreateBuilder(options.AspNetArgs.SplitArgs());
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(transkribusClient);
        builder.Services.AddScoped<Processor>();
        var app = builder.Build();
        app.MapGet("/", (
            [FromHeader(Name = "X-Islandora-Args")] string args,
            [FromHeader(Name = "Apix-Ldp-Resource")] string fileUri,
            Processor processor
        ) =>
        {
            var parseResult = Parser.Default.ParseArguments<MicroservicePageOptions, MicroserviceOcrOptions>(args.SplitArgs());
            return parseResult.MapResult(
                async (MicroservicePageOptions options) =>
                {
                    var file = await processor.ProcessSinglePage(new Uri(fileUri), options);
                    return Results.File(file, "application/xml");
                },
                async (MicroserviceOcrOptions options) =>
                {
                    var file = await processor.CreateSinglePageOcr(new Uri(fileUri), options);
                    return Results.File(file, "text/plain");
                },
                error => Task.FromResult(Results.BadRequest())
            );
        });
        Console.WriteLine("Running microservice");
        app.Run();
    }
}