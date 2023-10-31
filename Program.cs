using System.Diagnostics;
using CommandLine;
using dotenv.net;

DotEnv.Load();
await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(ProcessDocument);

// TODO: another entry point for checking the database & querying transkribus for in-process pages

async Task ProcessDocument(Options options)
{
    var pidFilePath = await GetPagePids(options);
    var jpgDirectory = await GetAndConvertImageDatastreams(options, pidFilePath);
    // TODO: upload to transkribus, save the process ids in a database
}

async Task RunProcessAndCaptureErrors(ProcessStartInfo startInfo)
{
    startInfo.CreateNoWindow = true;
    startInfo.RedirectStandardError = true;
    var process = new Process {StartInfo = startInfo};
    var errorOutput = string.Empty;
    process.ErrorDataReceived += (_, args) => { errorOutput += args.Data; };
    var started = process.Start();
    if (!started)
    {
        throw new Exception($"Failed to start {startInfo.FileName} process.");
    }
    process.BeginErrorReadLine();
    await process.WaitForExitAsync();
    if (!string.IsNullOrWhiteSpace(errorOutput))
    {
        throw new Exception($"{startInfo.FileName} process errored: " + errorOutput);
    }
}

async Task<string> GetPagePids(Options options)
{
    var pidFilePath = Path.GetTempFileName();
    await RunProcessAndCaptureErrors(new ProcessStartInfo
    {
        FileName = "drush",
        Arguments = $"--root={options.Root} --user=$USER --uri={options.Uri} islandora_datastream_crud_fetch_pids --solr_query=\"RELS_EXT_isMemberOf_uri_ms:info\\:fedora/{options.Pid}\" --pid_file={pidFilePath}"
    });
    return pidFilePath;
}

async Task<DirectoryInfo> GetAndConvertImageDatastreams(Options options, string pidFilePath)
{
    var jp2Directory = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "transkribus_process_jp2s"));
    var jpgDirectory = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "transkribus_process_jpgs"));
    try
    {
        await RunProcessAndCaptureErrors(new ProcessStartInfo
        {
            FileName = "drush",
            Arguments = $"--root={options.Root} --user=$USER --uri={options.Uri} islandora_datastream_crud_fetch_datastreams --pid_file={pidFilePath} --datastreams_directory={jp2Directory} --dsid=JP2"
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