using Microsoft.AspNetCore.Mvc;
using CommandLine;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

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
        if (bool.TryParse(configuration["USE_JWT_AUTHENTICATION"], out var useJwt) && useJwt) 
        {
            if (string.IsNullOrEmpty(configuration["JWT_PUBLIC_KEY"])) 
            {
                throw new InvalidOperationException("JWT_PUBLIC_KEY is required for JWT Authentication.");
            }
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwtOptions => 
            {
                var pubkey = configuration["JWT_PUBLIC_KEY"]
                    .Replace("-----BEGIN PUBLIC KEY-----", "")
                    .Replace("-----END PUBLIC KEY-----", "");
                var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubkey), out var _);
                jwtOptions.TokenValidationParameters = new TokenValidationParameters 
                {
                    IssuerSigningKey = new RsaSecurityKey(rsa),
                    ValidateIssuer = false,
                    RequireAudience = false,
                    ValidateAudience = false
                };
            });
        }
        var app = builder.Build();
        app.MapGet("/", (
            [FromHeader(Name = "X-Islandora-Args")] string args,
            [FromHeader(Name = "Apix-Ldp-Resource")] string fileUri,
            [FromHeader(Name = "Accept")] string mimeType,
            Processor processor
        ) =>
        {
            var parseResult = Parser.Default.ParseArguments<MicroservicePageOptions, MicroserviceOcrOptions>(args.SplitArgs());
            return parseResult.MapResult(
                async (MicroservicePageOptions options) =>
                {
                    var file = await processor.ProcessSinglePage(new Uri(fileUri), options);
                    return Results.File(file, mimeType);
                },
                async (MicroserviceOcrOptions options) =>
                {
                    var file = await processor.CreateSinglePageOcr(new Uri(fileUri), options);
                    return Results.File(file, mimeType);
                },
                error => Task.FromResult(Results.BadRequest())
            );
        });
        Console.WriteLine("Running microservice");
        app.Run();
    }
}