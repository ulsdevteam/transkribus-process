using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using CommandLine;

static class Microservice {

    public static void Run(IConfiguration configuration, Database database, TranskribusClient transkribusClient, string[] args)
    {
        Console.WriteLine("Setting up microservice...");
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton(transkribusClient);
        builder.Services.AddScoped<Processor>();
        var app = builder.Build();
        app.UseHttpsRedirection();
        app.MapGet("/", ([FromHeader(Name = "X-Islandora-Args")] string args, Processor processor) => {
            Parser.Default.ParseArguments<ProcessOptions>(args.SplitArgs())
                .MapResult(options => processor.ProcessDocument(options), error => Task.CompletedTask);
        });
        Console.WriteLine("Running microservice");
        app.Run();
    }

}