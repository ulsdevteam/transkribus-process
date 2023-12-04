using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CommandLine;
using dotenv.net;
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
    var pidFilePath = Path.GetTempFileName();
    var jp2Directory = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "transkribus_process_jp2s"));
    var jpgDirectory = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "transkribus_process_jpgs"));
    try
    {
        await GetPagePids(options, pidFilePath);
        await GetJp2Datastreams(options, pidFilePath, jp2Directory);
        await ConvertJp2sToJpgs(jp2Directory, jpgDirectory);
        await SendImagesToTranskribus(jpgDirectory, options.HtrId, options.Overwrite);
    }
    finally
    {
        File.Delete(pidFilePath);
        jp2Directory.Delete(recursive: true);
        jpgDirectory.Delete(recursive: true);
    }
}

async Task CheckProgress(CheckOptions options)
{
    var altoDirectory = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "transkribus_process_altos"));;
    var hocrDirectory = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "transkribus_process_hocrs"));;
    try
    {
        await GetTranskribusAltoXml(altoDirectory);
        if (!database.ChangeTracker.HasChanges()) { return; }
        await ConvertAltoToHocr(altoDirectory, hocrDirectory);
        await PushHocrDatastreams(options, hocrDirectory);
        await database.SaveChangesAsync();
    }
    finally
    {
        altoDirectory.Delete(recursive: true);
        hocrDirectory.Delete(recursive: true);
    }
}

async Task RunProcessAndCaptureErrors(ProcessStartInfo startInfo)
{
    var commandName = startInfo.FileName;
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        startInfo.FileName = "cmd";
        startInfo.Arguments = $"/c {commandName} {startInfo.Arguments}";
    }
    startInfo.CreateNoWindow = true;
    startInfo.RedirectStandardError = true;
    var process = new Process { StartInfo = startInfo };
    var errorOutput = string.Empty;
    process.ErrorDataReceived += (_, args) => { errorOutput += Environment.NewLine + args.Data; };
    if (!process.Start())
    {
        throw new Exception($"Failed to start {commandName} process.");
    }
    process.BeginErrorReadLine();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new Exception($"{commandName} process errored with code {process.ExitCode}{(string.IsNullOrWhiteSpace(errorOutput) ? "." : ": " + errorOutput)}");
    }
}

async Task GetPagePids(ProcessOptions options, string pidFilePath)
{
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user={options.User} --uri={options.Uri} idcrudfp --solr_query=\"RELS_EXT_isMemberOf_uri_ms:info\\:fedora/{options.Pid.Replace(":", "\\:")}\" --pid_file={pidFilePath}"
    });
}

async Task GetJp2Datastreams(IdCrudOptions options, string pidFilePath, DirectoryInfo jp2Directory)
{
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user={options.User} --uri={options.Uri} idcrudfd --pid_file={pidFilePath} --datastreams_directory={jp2Directory.FullName} --dsid=JP2"
    });
}

async Task ConvertJp2sToJpgs(DirectoryInfo jp2Directory, DirectoryInfo jpgDirectory)
{
    foreach (var jp2file in jp2Directory.EnumerateFiles())
    {
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "convert",
            Arguments = $"{jp2file.FullName} {Path.Join(jpgDirectory.FullName, Path.GetFileNameWithoutExtension(jp2file.Name) + ".jpg")}"
        });
    }
}

async Task SendImagesToTranskribus(DirectoryInfo jpgDirectory, int htrId, bool overwrite = false)
{
    foreach (var file in jpgDirectory.EnumerateFiles())
    {
        var pid = Regex.Replace(file.Name, "_JP2.jpg$", "");
        if (!overwrite && database.Pages.Where(p => p.Pid == pid).ToList() is List<Page> existingPages && existingPages.Any())
        {
            Console.Error.WriteLine(
                $"Page {pid} has already been uploaded to Transkribus " +
                string.Join(", ", existingPages.Select(existingPage =>
                    (existingPage.HtrId == htrId ? "with the same model " : $"with another model ({existingPage.HtrId}) ") +
                    (existingPage.InProgress ? "and is currently processing" : "and HOCR datastreams have already been pushed"))) + ".");
            Console.Error.WriteLine("Run with the --overwrite flag to disregard this and re-upload them.");
            continue;
        }
        var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullName));
        var processId = await transkribusClient.Process(htrId, imageBase64);
        database.Pages.Add(new Page
        {
            Pid = pid,
            HtrId = htrId,
            ProcessId = processId,
            InProgress = true
        });
        await database.SaveChangesAsync();
    }
}

async Task GetTranskribusAltoXml(DirectoryInfo altoDirectory)
{    
    foreach (var page in database.Pages.Where(p => p.InProgress))
    {
        var status = await transkribusClient.GetProcessStatus(page.ProcessId);
        if (status != "FINISHED")
        {
            continue;
        }
        var altoXml = await transkribusClient.GetAltoXml(page.ProcessId);
        var altoFileName = Path.Join(altoDirectory.FullName, page.Pid + "_ALTO.xml");
        altoXml.Save(File.OpenWrite(altoFileName));
        page.InProgress = false;
    }    
}

async Task ConvertAltoToHocr(DirectoryInfo altoDirectory, DirectoryInfo hocrDirectory)
{
    foreach (var altoFile in altoDirectory.EnumerateFiles())
    {
        var hocrFileName = Path.Join(hocrDirectory.FullName, Regex.Replace(altoFile.Name, "_ALTO.xml$", "_HOCR.shtml"));
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "xslt3",
            Arguments = $"-xsl:{config["ALTO_TO_HOCR_SEF_PATH"]} -s:{altoFile.FullName} -o:{hocrFileName}"
        });
    }
}

async Task PushHocrDatastreams(IdCrudOptions options, DirectoryInfo hocrDirectory)
{
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user={options.User} --uri={options.Uri} idcrudpd --datastreams_source_directory={hocrDirectory.FullName}"
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
    var altoDirectory = new DirectoryInfo(options.AltoDirectory);
    await GetTranskribusAltoXml(altoDirectory);
    await ConvertAltoToHocr(altoDirectory, hocrDirectory);
    await database.SaveChangesAsync();
}

async Task TestXslt(TestXsltOptions options)
{
    var hocrDirectory = new DirectoryInfo(options.HocrDirectory);
    var altoDirectory = new DirectoryInfo(options.AltoDirectory);
    foreach (var altoXml in altoDirectory.EnumerateFiles())
    {
        var hocrFileName = Path.Join(hocrDirectory.FullName, altoXml.Name.Replace("_ALTO.xml", "_HOCR.shtml"));
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "xslt3",
            Arguments = $"-xsl:{config["ALTO_TO_HOCR_SEF_PATH"]} -s:{altoXml.FullName} -o:{hocrFileName}"
        });
    }
}