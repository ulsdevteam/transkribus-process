using Microsoft.AspNetCore.Mvc;
using CommandLine;

static class Microservice {

    public static void Run(IConfiguration configuration, Database database, TranskribusClient transkribusClient, MsvcOptions options)
    {
        Console.WriteLine("Setting up microservice...");
        var builder = WebApplication.CreateBuilder(options.AspNetArgs.SplitArgs());
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(transkribusClient);
        builder.Services.AddScoped<Processor>();
        var app = builder.Build();
        app.UseHttpsRedirection();
        app.MapGet("/", ([FromHeader(Name = "X-Islandora-Args")] string args, Processor processor) => {
            Parser.Default.ParseArguments<ProcessPageOptions>(args.SplitArgs())
                .MapResult(async options => 
                {
                    var file = await processor.ProcessSinglePage(options);
                    return Results.File(file, "application/xml");
                }, error => Task.FromResult(Results.BadRequest(error)));
        });
        Console.WriteLine("Running microservice");
        app.Run();
    }

}