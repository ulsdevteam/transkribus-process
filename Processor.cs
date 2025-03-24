using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Flurl.Http;

class Processor
{
    IConfiguration Config { get; }
    Database Database { get; }
    TranskribusClient TranskribusClient { get; }
    public Guid Uuid { get; }
    public string Jp2Directory { get; }
    public string JpgDirectory { get; }
    public string AltoDirectory { get; }
    public string HocrDirectory { get; }
    public string OcrDirectory { get; }

    public Processor(IConfiguration config, Database database, TranskribusClient transkribusClient)
    {
        Config = config;
        Database = database;
        TranskribusClient = transkribusClient;
        Uuid = Guid.NewGuid();
        Jp2Directory = TempDirPath("jp2s");
        JpgDirectory = TempDirPath("jpgs");
        AltoDirectory = TempDirPath("altos");
        HocrDirectory = TempDirPath("hocrs");
        OcrDirectory = TempDirPath("ocrs");
    }

    string TempDirPath(string label) => Path.Join(Path.GetTempPath(), $"transkribus_process_{label}_{Uuid:N}");

    public async Task ProcessDocument(ProcessOptions options)
    {
        await UploadDocument(options);
        await Task.Delay(TimeSpan.FromSeconds(15));
        while (Database.Pages.Any(p => p.InProgress))
        {
            await CheckProgress(options);
        }
    }

    public async Task<byte[]> ProcessSinglePage(Uri fileUri, MicroservicePageOptions options)
    {
        try
        {
            var sourceFile = await fileUri.GetBytesAsync();
            Directory.CreateDirectory(Jp2Directory);
            await File.WriteAllBytesAsync(Path.Join(Jp2Directory, Path.GetFileName(fileUri.LocalPath)), sourceFile);
            await ConvertJp2sToJpgs();
            var page = await SendSinglePageToTranskribus(options);            
            await GetSinglePageTranskribusAltoXml(page);
            await ConvertAltoToHocr();
            ProcessHocrXml(new HocrHeaderFixer());
            var hocrFile = Directory.EnumerateFiles(HocrDirectory).Single();
            return await File.ReadAllBytesAsync(hocrFile);
        }
        finally
        {
            DeleteDirectoryIfExists(Jp2Directory);
            DeleteDirectoryIfExists(JpgDirectory);
            DeleteDirectoryIfExists(AltoDirectory);
            DeleteDirectoryIfExists(HocrDirectory);
        }
    }

    public async Task UploadDocument(UploadOptions options)
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
            DeleteDirectoryIfExists(Jp2Directory);
            DeleteDirectoryIfExists(JpgDirectory);
        }
    }

    public async Task CheckProgress(IdCrudOptions options)
    {
        try
        {
            await GetAllFinishedTranskribusAltoXml();
            if (!Directory.EnumerateFiles(AltoDirectory).Any())
            {
                await Database.SaveChangesAsync();
                return;
            }
            await ConvertAltoToHocr();
            ProcessHocrXml(new OcrGenerator(OcrDirectory), new HocrHeaderFixer());
            await PushHocrDatastreams(options);
            await Database.SaveChangesAsync();
            await PushOcrDatastreams(options);
        }
        finally
        {
            DeleteDirectoryIfExists(AltoDirectory);
            DeleteDirectoryIfExists(HocrDirectory);
            DeleteDirectoryIfExists(OcrDirectory);
        }
    }
    
    public async Task CreateOcrDatastreamsFromHocr(OcrOptions options)
    {
        string pidFilePath = null;
        try
        {
            pidFilePath = options.PidFile is null 
                ? await GetPagePids(options, options.Pid) 
                : Path.GetFullPath(options.PidFile);
            await GetHocrDatastreams(options, pidFilePath);
            ProcessHocrXml(new OcrGenerator(OcrDirectory));
            await PushOcrDatastreams(options);
        }
        finally
        {
            if (options.PidFile is null && pidFilePath is not null)
            {
                File.Delete(pidFilePath);
            }
            DeleteDirectoryIfExists(HocrDirectory);
            DeleteDirectoryIfExists(OcrDirectory);
        }
    }

    public async Task<byte[]> CreateSinglePageOcr(Uri fileUri, MicroserviceOcrOptions options)
    {
        try
        {
            var hocrFile = await fileUri.GetBytesAsync();
            Directory.CreateDirectory(HocrDirectory);
            await File.WriteAllBytesAsync(Path.Join(HocrDirectory, fileUri.LocalPath), hocrFile);
            ProcessHocrXml(new OcrGenerator(OcrDirectory));
            return await File.ReadAllBytesAsync(Directory.EnumerateFiles(OcrDirectory).Single());
        }
        finally
        {
            DeleteDirectoryIfExists(HocrDirectory);
            DeleteDirectoryIfExists(OcrDirectory);
        }
    }

    static void DeleteDirectoryIfExists(string path)
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

    static async Task RunProcessAndCaptureErrors(ProcessStartInfo startInfo)
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

    string CommonDrushOptions(IdCrudOptions options) =>
        $"--root={options.Root ?? Config["ISLANDORA_DRUPAL_ROOT"]} " +
        $"--user={options.User ?? Config["USER"]} " +
        $"--uri={options.Uri ?? Config["ISLANDORA_URI"]} ";

    /// <summary>
    /// Given the pid of an item, gets its pages and writes their pids to a temporary file.
    /// Uses IDCRUD Fetch Pids with a Solr search on RELS_EXT_isMemberOf_uri_ms
    /// </summary>
    /// <param name="options">Options to pass to Drush</param>
    /// <param name="pid">The item pid</param>
    /// <returns>Path to temporary file containing page pids</returns>
    async Task<string> GetPagePids(IdCrudOptions options, string pid)
    {
        var pidFilePath = Path.GetTempFileName();
        Console.WriteLine($"Getting page PIDs from {pid}...");
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "drush",
            Arguments = CommonDrushOptions(options) +
                        "idcrudfp " +
                        $"--solr_query=\"RELS_EXT_isMemberOf_uri_ms:info\\:fedora/{pid.Replace(":", "\\:")}\" " +
                        $"--pid_file={pidFilePath}"
        });
        return pidFilePath;
    }

    /// <summary>
    /// Downloads JP2 Datastreams into the Jp2Directory.
    /// Uses IDCRUD Fetch Datastreams
    /// </summary>
    /// <param name="options">Options to pass to Drush</param>
    /// <param name="pidFilePath">Path to page pid file</param>
    async Task GetJp2Datastreams(IdCrudOptions options, string pidFilePath)
    {
        Console.WriteLine("Fetching jp2 datastreams...");
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "drush",
            Arguments = CommonDrushOptions(options) +
                        "idcrudfd -y " +
                        $"--pid_file={pidFilePath} " +
                        $"--datastreams_directory={Jp2Directory} " +
                        "--dsid=JP2"
        });
    }

    /// <summary>
    /// Creates JpgDirectory and runs convert on each file in Jp2Directory.
    /// </summary>
    async Task ConvertJp2sToJpgs()
    {
        Console.WriteLine("Converting jp2s to jpgs...");
        Directory.CreateDirectory(JpgDirectory);
        foreach (var jp2file in Directory.EnumerateFiles(Jp2Directory))
        {
            await RunProcessAndCaptureErrors(new ProcessStartInfo
            {
                FileName = "convert",
                Arguments = $"{jp2file} {Path.Join(JpgDirectory, Path.GetFileNameWithoutExtension(jp2file) + ".jpg")}"
            });
        }
    }

    /// <summary>
    /// For every file in the JpgDirectory, upload it as base64 to the Transkribus API and save the process ID we get back in the DB.
    /// Associates the process ID with the pid by getting the pid from the JPG's filename
    /// </summary>
    /// <param name="htrId">Transkribus Handwriting Text Recognition Model ID</param>
    /// <param name="user">The current user</param>
    /// <param name="overwrite">When false, will check if a page with that pid has already been processed and will skip that page if so.</param>
    async Task SendImagesToTranskribus(int htrId, string user, bool overwrite = false)
    {
        Console.WriteLine("Uploading images to Transkribus...");
        foreach (var file in Directory.EnumerateFiles(JpgDirectory))
        {
            var pid = Regex.Replace(Path.GetFileName(file), "_JP2.jpg$", "");
            if (!overwrite)
            {
                var existingPages = Database.Pages.Where(p => p.Pid == pid && (p.InProgress || p.Downloaded.HasValue)).ToList();
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
            var processId = await TranskribusClient.Process(htrId, imageBase64);
            Database.Pages.Add(new Page
            {
                Pid = pid,
                HtrId = htrId,
                ProcessId = processId,
                InProgress = true,
                User = user,
                Uploaded = DateTime.Now
            });
            await Database.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Uploads a single file in the JpgDirectory as base64 to the Transkribus API and saves the Process ID.
    /// Does not associate the Process ID with a Pid.
    /// </summary>
    /// <param name="htrId">Transkribus Handwriting Text Recognition Model ID</param>
    /// <returns>The Page object containing the Process ID</returns>
    async Task<Page> SendSinglePageToTranskribus(MicroservicePageOptions options)
    {
        var file = Directory.EnumerateFiles(JpgDirectory).Single();
        var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(file));
        var processId = await TranskribusClient.Process(options, imageBase64);
        var page = new Page
        {
            HtrId = options.HtrId,
            ProcessId = processId,
            InProgress = true,
            Uploaded = DateTime.Now
        };
        Database.Pages.Add(page);
        await Database.SaveChangesAsync();
        return page;
    }

    /// <summary>
    /// Checks all Pages marked In Progress for completion, and downloads their ALTO XML to the AltoDirectory if they are.
    /// If a call to the Transkribus API returns a 404, that Page is marked as no longer being In Progress.
    /// </summary>
    async Task GetAllFinishedTranskribusAltoXml()
    {
        Console.WriteLine("Checking for finished pages...");
        Directory.CreateDirectory(AltoDirectory);
        foreach (var page in Database.Pages.Where(p => p.InProgress))
        {
            try
            {
                var status = await TranskribusClient.GetProcessStatus(page.ProcessId);
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
            var altoXml = await TranskribusClient.GetAltoXml(page.ProcessId);
            var altoFile = Path.Join(AltoDirectory, page.Pid + "_ALTO.xml");
            altoXml.Save(File.OpenWrite(altoFile));
            page.Downloaded = DateTime.Now;
            page.InProgress = false;
        }
    }

    /// <summary>
    /// Checks for the completion of one page in a loop, and when complete downloads its ALTO XML to the AltoDirectory.
    /// </summary>
    /// <param name="page">The Page object</param>
    async Task GetSinglePageTranskribusAltoXml(Page page)
    {
        while (await TranskribusClient.GetProcessStatus(page.ProcessId) != "FINISHED")
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        var altoXml = await TranskribusClient.GetAltoXml(page.ProcessId);
        Directory.CreateDirectory(AltoDirectory);
        var altoFile = Path.Join(AltoDirectory, page.Pid + "_ALTO.xml");
        altoXml.Save(File.OpenWrite(altoFile));
        page.Downloaded = DateTime.Now;
        page.InProgress = false;
        await Database.SaveChangesAsync();
    }

    /// <summary>
    /// Creates the HocrDirectory and uses xlst3 to transform each file in the AltoDirectory into HOCR.
    /// </summary>
    async Task ConvertAltoToHocr()
    {
        Console.WriteLine("Converting ALTO XML to hOCR...");
        Directory.CreateDirectory(HocrDirectory);
        foreach (var altoFile in Directory.EnumerateFiles(AltoDirectory))
        {
            var hocrFile = Path.Join(HocrDirectory, Regex.Replace(Path.GetFileName(altoFile), "_ALTO.xml$", "_HOCR.shtml"));
            await RunProcessAndCaptureErrors(new ProcessStartInfo
            {
                FileName = "xslt3",
                Arguments = $"-xsl:{Config["ALTO_TO_HOCR_SEF_PATH"]} -s:{altoFile} -o:{hocrFile}"
            });
        }
    }

    /// <summary>
    /// Given a set of IHocrXmlProcessors, runs them each on every file in the HocrDirectory.
    /// The purpose of this function is to avoid calling XDocument.Load on the same file multiple times
    /// </summary>
    /// <param name="processors">The HOCR XML Processors</param>
    void ProcessHocrXml(params IHocrXmlProcessor[] processors)
    {
        foreach (var processor in processors)
        {
            processor.Init();
        }
        foreach (var hocrFile in Directory.EnumerateFiles(HocrDirectory))
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
            Arguments = CommonDrushOptions(options) +
                        "idcrudpd " +
                        $"--datastreams_source_directory={HocrDirectory}"
        });
    }

    async Task GetHocrDatastreams(IdCrudOptions options, string pidFilePath)
    {
        Console.WriteLine("Fetching HOCR datastreams...");
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "drush",
            Arguments = CommonDrushOptions(options) +
                        "idcrudfd -y " +
                        $"--pid_file={pidFilePath} " +
                        $"--datastreams_directory={HocrDirectory} " +
                        "--dsid=HOCR"
        });
    }

    async Task PushOcrDatastreams(IdCrudOptions options)
    {
        Console.WriteLine("Pushing OCR datastreams to Islandora...");
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "drush",
            Arguments = CommonDrushOptions(options) +
                        "idcrudpd " +
                        $"--datastreams_source_directory={OcrDirectory}"
        });
    }
}