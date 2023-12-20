using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
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
var uuid = Guid.NewGuid();
await Parser.Default.ParseArguments<ProcessOptions, UploadOptions, CheckOptions>(args).MapResult(
    (ProcessOptions options) => ProcessDocument(options),
    (UploadOptions options) => UploadDocument(options),
    (CheckOptions options) => CheckProgress(options),
    (errors) => Task.CompletedTask
);

async Task ProcessDocument(ProcessOptions options)
{
    await UploadDocument(options);
    await Task.Delay(TimeSpan.FromSeconds(15));
    while (database.Pages.Any(p => p.InProgress)) 
    {
        await CheckProgress(options);
    }
}

async Task UploadDocument(UploadOptions options)
{
    var pidFilePath = Path.GetTempFileName();
    var jp2Directory = Directory.CreateDirectory(TempDirPath("jp2s"));
    var jpgDirectory = Directory.CreateDirectory(TempDirPath("jpgs"));
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

async Task CheckProgress(IdCrudOptions options)
{
    var altoDirectory = Directory.CreateDirectory(TempDirPath("altos"));
    var hocrDirectory = Directory.CreateDirectory(TempDirPath("hocrs"));
    try
    {
        await GetTranskribusAltoXml(altoDirectory);
        if (!database.ChangeTracker.HasChanges()) { return; }
        await ConvertAltoToHocr(altoDirectory, hocrDirectory);
        FixHocrFiles(hocrDirectory);
        await PushHocrDatastreams(options, hocrDirectory);
        await database.SaveChangesAsync();
    }
    finally
    {
        altoDirectory.Delete(recursive: true);
        hocrDirectory.Delete(recursive: true);
    }
}

string TempDirPath(string label) => Path.Join(Path.GetTempPath(), $"transkribus_process_{label}_{uuid:N}");

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

async Task GetPagePids(UploadOptions options, string pidFilePath)
{
    Console.WriteLine($"Getting page PIDs from {options.Pid}...");
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user={options.User} --uri={options.Uri} idcrudfp --solr_query=\"RELS_EXT_isMemberOf_uri_ms:info\\:fedora/{options.Pid.Replace(":", "\\:")}\" --pid_file={pidFilePath}"
    });
}

async Task GetJp2Datastreams(IdCrudOptions options, string pidFilePath, DirectoryInfo jp2Directory)
{
    Console.WriteLine("Fetching jp2 datastreams...");
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user={options.User} --uri={options.Uri} idcrudfd --pid_file={pidFilePath} --datastreams_directory={jp2Directory.FullName} --dsid=JP2"
    });
}

async Task ConvertJp2sToJpgs(DirectoryInfo jp2Directory, DirectoryInfo jpgDirectory)
{
    Console.WriteLine("Converting jp2s to jpgs...");
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
    Console.WriteLine("Uploading images to Transkribus...");
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
    Console.WriteLine("Checking for finished pages...");
    foreach (var page in database.Pages.Where(p => p.InProgress))
    {
        var status = await transkribusClient.GetProcessStatus(page.ProcessId);
        if (status != "FINISHED")
        {
            continue;
        }
        Console.WriteLine($"{page.Pid} is done processing, downloading...");
        var altoXml = await transkribusClient.GetAltoXml(page.ProcessId);
        var altoFileName = Path.Join(altoDirectory.FullName, page.Pid + "_ALTO.xml");
        altoXml.Save(File.OpenWrite(altoFileName));
        page.InProgress = false;
    }    
}

async Task ConvertAltoToHocr(DirectoryInfo altoDirectory, DirectoryInfo hocrDirectory)
{
    Console.WriteLine("Converting ALTO XML to hOCR...");
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

void FixHocrFiles(DirectoryInfo hocrDirectory)
{
    Console.WriteLine("Fixing hOCR file headers...");
    foreach (var hocrFile in hocrDirectory.EnumerateFiles())
    {
        var readStream = hocrFile.OpenRead();
        var xml = XDocument.Load(readStream);
        readStream.Close();
        XNamespace ns = "http://www.w3.org/1999/xhtml";
        var head = xml.Element(ns + "html").Element(ns + "head");
        head.Element(ns + "title").Value = "Image: " + Regex.Replace(hocrFile.Name, "_HOCR.shtml$", "_JP2.jpg");
        head.Add(new XElement(ns + "meta", new XAttribute("name", "ocr-system"), new XAttribute("content", "Transkribus")));
        var writer = XmlWriter.Create(hocrFile.Open(FileMode.Truncate), new XmlWriterSettings 
        {
            Encoding = new UTF8Encoding(false), 
            Indent = true
        });
        xml.Save(writer);
        writer.Close();
    }
}

async Task PushHocrDatastreams(IdCrudOptions options, DirectoryInfo hocrDirectory)
{
    Console.WriteLine("Pushing hOCR datastreams to Islandora...");
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user={options.User} --uri={options.Uri} idcrudpd --datastreams_source_directory={hocrDirectory.FullName}"
    });
}

// async Task TestWithJpgs(TestUploadOptions options)
// {
//     var jpgDirectory = new DirectoryInfo(options.JpgDirectory);
//     await SendImagesToTranskribus(jpgDirectory, options.HtrId, options.Overwrite);
// }

// async Task TestDownload(TestDownloadOptions options)
// {
//     var hocrDirectory = new DirectoryInfo(options.HocrDirectory);
//     var altoDirectory = new DirectoryInfo(options.AltoDirectory);
//     await GetTranskribusAltoXml(altoDirectory);
//     await ConvertAltoToHocr(altoDirectory, hocrDirectory);
//     await database.SaveChangesAsync();
// }

// async Task TestXslt(TestXsltOptions options)
// {
//     var hocrDirectory = new DirectoryInfo(options.HocrDirectory);
//     var altoDirectory = new DirectoryInfo(options.AltoDirectory);
//     foreach (var altoXml in altoDirectory.EnumerateFiles())
//     {
//         var hocrFileName = Path.Join(hocrDirectory.FullName, altoXml.Name.Replace("_ALTO.xml", "_HOCR.shtml"));
//         await RunProcessAndCaptureErrors(new ProcessStartInfo
//         {
//             FileName = "xslt3",
//             Arguments = $"-xsl:{config["ALTO_TO_HOCR_SEF_PATH"]} -s:{altoXml.FullName} -o:{hocrFileName}"
//         });
//     }
//     FixHocrFiles(hocrDirectory);
// }
