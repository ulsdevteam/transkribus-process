using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Xsl;
using CommandLine;
using dotenv.net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

DotEnv.Load();
var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();
var database = new Database(config["DATABASE_PATH"]);
await database.Database.EnsureCreatedAsync();
var transkribusClient = new TranskribusClient(config["TRANSKRIBUS_USERNAME"], config["TRANSKRIBUS_PASSWORD"]);
await Parser.Default.ParseArguments<ProcessOptions, CheckOptions, TestUploadOptions, TestDownloadOptions, TestXsltOptions>(args).MapResult(
    (ProcessOptions options) => ProcessDocument(options),
    (CheckOptions options) => CheckProgress(options),
    (TestUploadOptions options) => TestWithJpgs(options),
    (TestDownloadOptions options) => TestDownload(options),
    (TestXsltOptions options) => TestXslt(options),
    (errors) => Task.CompletedTask
);

async Task ProcessDocument(ProcessOptions options)
{
    string pidFilePath = null;
    DirectoryInfo jpgDirectory = null;
    try
    {
        pidFilePath = await GetPagePids(options);
        jpgDirectory = await GetAndConvertImageDatastreams(options, pidFilePath);
        await SendImagesToTranskribus(jpgDirectory, options.HtrId, options.Overwrite);
    }
    finally
    {        
        if (pidFilePath is not null)
        {
            File.Delete(pidFilePath);
        }
        jpgDirectory?.Delete(recursive: true);
    }
}

async Task RunProcessAndCaptureErrors(ProcessStartInfo startInfo)
{
    startInfo.CreateNoWindow = true;
    startInfo.RedirectStandardError = true;
    var process = new Process {StartInfo = startInfo};
    var errorOutput = string.Empty;
    process.ErrorDataReceived += (_, args) => { errorOutput += Environment.NewLine + args.Data; };
    if (!process.Start())
    {
        throw new Exception($"Failed to start {startInfo.FileName} process.");
    }
    process.BeginErrorReadLine();
    await process.WaitForExitAsync();
    if (!string.IsNullOrWhiteSpace(errorOutput))
    {
        throw new Exception($"{startInfo.FileName} process errored:" + errorOutput);
    }
}

async Task<string> GetPagePids(ProcessOptions options)
{
    var pidFilePath = Path.GetTempFileName();
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user=$USER --uri={options.Uri} idcrudfp --solr_query=\"RELS_EXT_isMemberOf_uri_ms:info\\:fedora/{options.Pid.Replace(":", "\\:")}\" --pid_file={pidFilePath}"
    });
    return pidFilePath;
}

async Task<DirectoryInfo> GetAndConvertImageDatastreams(IdCrudOptions options, string pidFilePath)
{
    var jp2Directory = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "transkribus_process_jp2s"));
    var jpgDirectory = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "transkribus_process_jpgs"));
    try
    {
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "drush",
            Arguments = $"--root={options.Root} --user=$USER --uri={options.Uri} idcrudfd --pid_file={pidFilePath} --datastreams_directory={jp2Directory.FullName} --dsid=JP2"
        });
        foreach (var jp2file in jp2Directory.EnumerateFiles())
        {
            await RunProcessAndCaptureErrors(new ProcessStartInfo
            {
                FileName = "convert",
                Arguments = $"{jp2file.FullName} {Path.Join(jpgDirectory.FullName, Path.GetFileNameWithoutExtension(jp2file.Name) + ".jpg")}"
            });
        }
    }
    catch
    {
        jpgDirectory.Delete(recursive: true);
        throw;
    }
    finally
    {
        jp2Directory.Delete(recursive: true);
    }
    return jpgDirectory;
}

async Task SendImagesToTranskribus(DirectoryInfo jpgDirectory, int htrId, bool overwrite = false)
{
    foreach (var file in jpgDirectory.EnumerateFiles())
    {
        var pid = Regex.Replace(file.Name, "_JP2.jpg$", "");
        if (!overwrite && database.Pages.FirstOrDefault(p => p.Pid == pid) is Page existingPage)
        {
            Console.Error.WriteLine($"Page {pid} has already been uploaded to Transkribus {(existingPage.InProgress ? "and is currently processing" : "and HOCR datastreams have already been pushed")}.");
            Console.Error.WriteLine("Run again with the --overwrite flag to disregard this and re-upload them.");
            continue;
        }
        var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullName));
        var processId = await transkribusClient.Process(htrId, imageBase64);
        database.Pages.Add(new Page
        {
            Pid = pid,
            ProcessId = processId,
            InProgress = true
        });
        await database.SaveChangesAsync();
    }
}

async Task CheckProgress(CheckOptions options)
{
    var hocrDirectory = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "transkribus_process_hocrs"));
    try
    {
        await GetAndConvertTranskribusHocr(hocrDirectory);
        await PushHocrDatastreams(options, hocrDirectory);
        await database.SaveChangesAsync();
    }
    finally
    {
        hocrDirectory.Delete(recursive: true);
    }
}

async Task GetAndConvertTranskribusHocr(DirectoryInfo hocrDirectory, DirectoryInfo altoDirectory = null)
{
    var xslt = LoadXslt();
    foreach (var page in database.Pages.Where(p => p.InProgress))
    {
        var status = await transkribusClient.GetProcessStatus(page.ProcessId);
        if (status != "FINISHED")
        {
            continue;
        }
        var altoXml = await transkribusClient.GetAltoXml(page.ProcessId);
        if (altoDirectory is not null)
        {
            var altoFileName = Path.Join(altoDirectory.FullName, page.Pid + "_ALTO.xml");
            altoXml.Save(File.OpenWrite(altoFileName));            
        }
        var hocrFileName = Path.Join(hocrDirectory.FullName, page.Pid + "_HOCR.shtml");
        xslt.Transform(altoXml.CreateReader(), XmlWriter.Create(hocrFileName));
        page.InProgress = false;
    }
}

XslCompiledTransform LoadXslt()
{
    var stream = Assembly.GetExecutingAssembly()
        .GetManifestResourceStream("transkribus_process.hOCR_to_ALTO.alto__hocr.xsl");
    var reader = XmlReader.Create(stream);
    var transformer = new XslCompiledTransform(enableDebug: true);
    transformer.Load(reader);
    return transformer; 
}

async Task PushHocrDatastreams(IdCrudOptions options, DirectoryInfo hocrDirectory)
{
    await RunProcessAndCaptureErrors(new ProcessStartInfo 
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user=$USER --uri={options.Uri} idcrudpd --datastreams_source_directory={hocrDirectory.FullName}"
    });
}

async Task TestWithJpgs(TestUploadOptions options)
{
    var jpgDirectory = new DirectoryInfo(options.JpgDirectory);
    await SendImagesToTranskribus(jpgDirectory, options.HtrId, options.Overwrite);
}

async Task TestDownload(TestDownloadOptions options)
{
    var hocrDirectory = new DirectoryInfo(options.HocrDirectory);
    var altoDirectory = options.AltoDirectory is not null ? new DirectoryInfo(options.AltoDirectory) : null;
    await GetAndConvertTranskribusHocr(hocrDirectory, altoDirectory);
    await database.SaveChangesAsync();
}

async Task TestXslt(TestXsltOptions options)
{
    var hocrDirectory = new DirectoryInfo(options.HocrDirectory);
    var altoDirectory = new DirectoryInfo(options.AltoDirectory);
    var xslt = LoadXslt();
    foreach (var altoXml in altoDirectory.EnumerateFiles())
    {
        var hocrFileName = Path.Join(hocrDirectory.FullName, altoXml.Name.Replace("_ALTO.xml", "_HOCR.shtml"));
        xslt.Transform(XmlReader.Create(altoXml.OpenText()), XmlWriter.Create(hocrFileName));
    }
}