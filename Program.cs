using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CommandLine;
using dotenv.net;
using Flurl.Http;
using Microsoft.Extensions.Configuration;

DotEnv.Load();
var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();
var database = new Database(config["DATABASE_PATH"]);
await database.Database.EnsureCreatedAsync();
var transkribusClient = new TranskribusClient(config["TRANSKRIBUS_USERNAME"], config["TRANSKRIBUS_PASSWORD"]);
// Define paths for temp folders, a uuid is included in case there are multiple instances running simultaneously
var uuid = Guid.NewGuid();
string TempDirPath(string label) => Path.Join(Path.GetTempPath(), $"transkribus_process_{label}_{uuid:N}");
var jp2Directory = TempDirPath("jp2s");
var jpgDirectory = TempDirPath("jpgs");
var altoDirectory = TempDirPath("altos");
var hocrDirectory = TempDirPath("hocrs");
var ocrDirectory = TempDirPath("ocrs");
await Parser.Default.ParseArguments<ProcessOptions, UploadOptions, CheckOptions, OcrOptions>(args).MapResult(
    (ProcessOptions options) => ProcessDocument(options),
    (UploadOptions options) => UploadDocument(options),
    (CheckOptions options) => CheckProgress(options),
    (OcrOptions options) => CreateOcrDatastreamsFromHocr(options),
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
    string pidFilePath = null;
    try
    {
        pidFilePath = options.PidFile is null ? await GetPagePids(options, options.Pid) : Path.GetFullPath(options.PidFile);
        await GetJp2Datastreams(options, pidFilePath);
        await ConvertJp2sToJpgs();
        await SendImagesToTranskribus(options.HtrId, options.User, options.Overwrite);
    }
    finally
    {
        if (options.PidFile is null && pidFilePath is not null)
        {
            File.Delete(pidFilePath);        
        }
        DeleteDirectoryIfExists(jp2Directory);
        DeleteDirectoryIfExists(jpgDirectory);
    }
}

async Task CheckProgress(IdCrudOptions options)
{
    try
    {
        await GetTranskribusAltoXml();
        if (!Directory.EnumerateFiles(altoDirectory).Any()) 
        {
            await database.SaveChangesAsync();
            return; 
        }
        await ConvertAltoToHocr();
        ProcessHocrXml(new OcrGenerator(ocrDirectory), new HocrHeaderFixer());
        await PushHocrDatastreams(options);
        await database.SaveChangesAsync();
        await PushOcrDatastreams(options);
    }
    finally
    {
        DeleteDirectoryIfExists(altoDirectory);
        DeleteDirectoryIfExists(hocrDirectory);
        DeleteDirectoryIfExists(ocrDirectory);
    }
}

async Task CreateOcrDatastreamsFromHocr(OcrOptions options)
{
    string pidFilePath = null;
    try
    {
        pidFilePath = options.PidFile is null ? await GetPagePids(options, options.Pid) : Path.GetFullPath(options.PidFile);
        await GetHocrDatastreams(options, pidFilePath);
        ProcessHocrXml(new OcrGenerator(ocrDirectory));
        await PushOcrDatastreams(options);
    }
    finally
    {
        if (options.PidFile is null && pidFilePath is not null)
        {
            File.Delete(pidFilePath);        
        }
        DeleteDirectoryIfExists(hocrDirectory);
        DeleteDirectoryIfExists(ocrDirectory);
    }
}

void DeleteDirectoryIfExists(string path)
{
    try
    {
        Directory.Delete(path, true);
    }
    catch (DirectoryNotFoundException)
    {
        // this space intentionally left blank
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
    var errorOutput = new StringBuilder();
    process.ErrorDataReceived += (_, args) => errorOutput.AppendLine(args.Data);
    if (!process.Start())
    {
        throw new Exception($"Failed to start {commandName} process.");
    }
    process.BeginErrorReadLine();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new Exception($"{commandName} process errored with code {process.ExitCode}{(errorOutput.Length == 0 ? "." : ": " + errorOutput)}");
    }
}

async Task<string> GetPagePids(IdCrudOptions options, string pid)
{
    var pidFilePath = Path.GetTempFileName();
    Console.WriteLine($"Getting page PIDs from {pid}...");
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user={options.User} --uri={options.Uri} idcrudfp --solr_query=\"RELS_EXT_isMemberOf_uri_ms:info\\:fedora/{pid.Replace(":", "\\:")}\" --pid_file={pidFilePath}"
    });
    return pidFilePath;
}

async Task GetJp2Datastreams(IdCrudOptions options, string pidFilePath)
{
    Console.WriteLine("Fetching jp2 datastreams...");
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"-y --root={options.Root} --user={options.User} --uri={options.Uri} idcrudfd --pid_file={pidFilePath} --datastreams_directory={jp2Directory} --dsid=JP2"
    });
}

async Task ConvertJp2sToJpgs()
{
    Console.WriteLine("Converting jp2s to jpgs...");
    Directory.CreateDirectory(jpgDirectory);
    foreach (var jp2file in Directory.EnumerateFiles(jp2Directory))
    {
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "convert",
            Arguments = $"{jp2file} {Path.Join(jpgDirectory, Path.GetFileNameWithoutExtension(jp2file) + ".jpg")}"
        });
    }
}

async Task SendImagesToTranskribus(int htrId, string user, bool overwrite = false)
{
    Console.WriteLine("Uploading images to Transkribus...");
    foreach (var file in Directory.EnumerateFiles(jpgDirectory))
    {
        var pid = Regex.Replace(Path.GetFileName(file), "_JP2.jpg$", "");
        if (!overwrite)
        {
            var existingPages = database.Pages.Where(p => p.Pid == pid && (p.InProgress || p.Downloaded.HasValue)).ToList();
            if (existingPages.Any())
            {                
                Console.Error.WriteLine(
                    $"Page {pid} has already been uploaded to Transkribus " +
                    string.Join(", ", existingPages.Select(existingPage =>
                        (existingPage.HtrId == htrId ? "with the same model " : $"with another model ({existingPage.HtrId}) ") +
                        (existingPage.InProgress ? "and is currently processing" : "and HOCR datastreams have already been pushed"))) + ".");
                Console.Error.WriteLine("Run with the --overwrite flag to disregard this and re-upload them.");
                continue;
            }
        }
        var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(file));
        var processId = await transkribusClient.Process(htrId, imageBase64);
        database.Pages.Add(new Page
        {
            Pid = pid,
            HtrId = htrId,
            ProcessId = processId,
            InProgress = true,
            User = user,
            Uploaded = DateTime.Now
        });
        await database.SaveChangesAsync();
    }
}

async Task GetTranskribusAltoXml()
{    
    Console.WriteLine("Checking for finished pages...");
    Directory.CreateDirectory(altoDirectory);
    foreach (var page in database.Pages.Where(p => p.InProgress))
    {
        try
        {
            var status = await transkribusClient.GetProcessStatus(page.ProcessId);
            if (status != "FINISHED")
            {
                continue;
            }
        }
        catch (FlurlHttpException e) when (e.StatusCode == 404)
        {
            Console.Error.WriteLine($"Transkribus process for page {page.Pid} has expired.");
            page.InProgress = false;
            continue;
        }
        Console.WriteLine($"{page.Pid} is done processing, downloading...");
        var altoXml = await transkribusClient.GetAltoXml(page.ProcessId);
        var altoFile = Path.Join(altoDirectory, page.Pid + "_ALTO.xml");
        altoXml.Save(File.OpenWrite(altoFile));
        page.Downloaded = DateTime.Now;
        page.InProgress = false;
    }    
}

async Task ConvertAltoToHocr()
{
    Console.WriteLine("Converting ALTO XML to hOCR...");
    Directory.CreateDirectory(hocrDirectory);
    foreach (var altoFile in Directory.EnumerateFiles(altoDirectory))
    {
        var hocrFile = Path.Join(hocrDirectory, Regex.Replace(Path.GetFileName(altoFile), "_ALTO.xml$", "_HOCR.shtml"));
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "xslt3",
            Arguments = $"-xsl:{config["ALTO_TO_HOCR_SEF_PATH"]} -s:{altoFile} -o:{hocrFile}"
        });
    }
}

// the purpose of this function is to avoid calling XDocument.Load on the same file multiple times
void ProcessHocrXml(params IHocrXmlProcessor[] processors)
{
    foreach (var processor in processors)
    {
        processor.Init();
    }
    foreach (var hocrFile in Directory.EnumerateFiles(hocrDirectory))
    {
        var xml = XDocument.Load(hocrFile);
        foreach (var processor in processors)
        {
            processor.Process(hocrFile, xml);
        }
    }
}

async Task PushHocrDatastreams(IdCrudOptions options)
{
    Console.WriteLine("Pushing HOCR datastreams to Islandora...");
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user={options.User} --uri={options.Uri} idcrudpd --datastreams_source_directory={hocrDirectory}"
    });
}

async Task GetHocrDatastreams(IdCrudOptions options, string pidFilePath)
{
    Console.WriteLine("Fetching HOCR datastreams...");
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"-y --root={options.Root} --user={options.User} --uri={options.Uri} idcrudfd --pid_file={pidFilePath} --datastreams_directory={hocrDirectory} --dsid=HOCR"
    });
}

async Task PushOcrDatastreams(IdCrudOptions options)
{
    Console.WriteLine("Pushing OCR datastreams to Islandora...");
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user={options.User} --uri={options.Uri} idcrudpd --datastreams_source_directory={ocrDirectory}"
    });
}

interface IHocrXmlProcessor
{
    void Init();
    void Process(string hocrFile, XDocument hocrXml);
}

class HocrHeaderFixer : IHocrXmlProcessor
{
    public void Init()
    {
        Console.WriteLine("Fixing hOCR file headers...");
    }

    public void Process(string hocrFile, XDocument hocrXml)
    {
        XNamespace ns = "http://www.w3.org/1999/xhtml";
        var head = hocrXml.Element(ns + "html").Element(ns + "head");
        head.Element(ns + "title").Value = "Image: " + Regex.Replace(Path.GetFileName(hocrFile), "_HOCR.shtml$", "_JP2.jpg");
        head.Add(new XElement(ns + "meta", new XAttribute("name", "ocr-system"), new XAttribute("content", "Transkribus")));
        var writer = XmlWriter.Create(File.Open(hocrFile, FileMode.Truncate), new XmlWriterSettings 
        {
            // need to specify false here to stop it from emitting a byte order mark
            Encoding = new UTF8Encoding(false), 
            Indent = true
        });
        hocrXml.Save(writer);
        writer.Close();
    }
}

class OcrGenerator : IHocrXmlProcessor
{
    private string OcrDirectory {get;}
    public OcrGenerator(string ocrDirectory)
    {
        OcrDirectory = ocrDirectory;
    }

    public void Init()
    {
        Console.WriteLine("Generating OCR files from hOCR files...");
        Directory.CreateDirectory(OcrDirectory);
    }

    public void Process(string hocrFile, XDocument hocrXml)
    {
        var text = new StringBuilder();
        XNamespace ns = "http://www.w3.org/1999/xhtml";
        foreach (var paragraph in hocrXml.Descendants(ns + "p"))
        {
            var lines = paragraph.Elements(ns + "span").Where(span => span.Attribute("class")?.Value == "ocr_line"); 
            foreach (var line in lines) 
            {
                var words = line.Elements(ns + "span").Select(span => span.Value);
                text.AppendJoin(' ', words);
                text.AppendLine();
            }
            text.AppendLine();
        }
        var ocrFile = Path.Join(OcrDirectory, Regex.Replace(Path.GetFileName(hocrFile), "_HOCR.shtml$", "_OCR.asc"));
        File.WriteAllText(ocrFile, text.ToString().Trim());
    }
}

// void TestOcr(TestOcrOptions options)
// {
//     hocrDirectory = options.HocrDirectory;
//     ocrDirectory = options.OcrDirectory;
//     GenerateOcrFiles();
// }

// async Task TestWithJpgs(TestUploadOptions options)
// {
//     jpgDirectory = options.JpgDirectory;
//     await SendImagesToTranskribus(options.HtrId, options.Overwrite);
// }

// async Task TestDownload(TestDownloadOptions options)
// {
//     hocrDirectory = options.HocrDirectory;
//     altoDirectory = options.AltoDirectory;
//     await GetTranskribusAltoXml();
//     await ConvertAltoToHocr();
//     await database.SaveChangesAsync();
// }

// async Task TestXslt(TestXsltOptions options)
// {
//     hocrDirectory = options.HocrDirectory;
//     altoDirectory = options.AltoDirectory;
//     foreach (var altoXml in Directory.EnumerateFiles(altoDirectory))
//     {
//         var hocrFileName = Path.Join(hocrDirectory, Path.GetFileName(altoXml).Replace("_ALTO.xml", "_HOCR.shtml"));
//         await RunProcessAndCaptureErrors(new ProcessStartInfo
//         {
//             FileName = "xslt3",
//             Arguments = $"-xsl:{config["ALTO_TO_HOCR_SEF_PATH"]} -s:{altoXml} -o:{hocrFileName}"
//         });
//     }
//     FixHocrFiles();
// }
