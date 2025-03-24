using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CommandLine;
using dotenv.net;
using Flurl.Http;
using Microsoft.Extensions.Configuration;

DotEnv.Load();
var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddKeyPerFile("/run/secrets", optional: true)
    .Build();
var database = new Database(config);
await database.Database.EnsureCreatedAsync();
var transkribusClient = new TranskribusClient(config["TRANSKRIBUS_USERNAME"], config["TRANSKRIBUS_PASSWORD"]);
await Parser.Default.ParseArguments<ProcessOptions, UploadOptions, CheckOptions, OcrOptions, MsvcOptions>(args)
    .MapResult(
        (ProcessOptions options) => new Processor(config, database, transkribusClient).ProcessDocument(options),
        (UploadOptions options) => new Processor(config, database, transkribusClient).UploadDocument(options),
        (CheckOptions options) => new Processor(config, database, transkribusClient).CheckProgress(options),
        (OcrOptions options) => new Processor(config, database, transkribusClient).CreateOcrDatastreamsFromHocr(options),
        (MsvcOptions options) => { Microservice.Run(config, database, transkribusClient, options); return Task.CompletedTask; },
        (errors) => Task.CompletedTask
    );
