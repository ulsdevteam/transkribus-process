using CommandLine;

abstract class IdCrudOptions
{
    [Option]
    public string Root { get; set; }

    [Option]
    public string Uri { get; set; }

    [Option]
    public string User { get; set; }
}

[Verb("process", isDefault: true)]
class ProcessOptions : UploadOptions
{

}

[Verb("upload")]
class UploadOptions : IdCrudOptions
{
    [Option(SetName = "pid")]
    public string Pid { get; set; }

    [Option(SetName = "pidfile")]
    public string PidFile { get; set; }

    [Option]
    public int HtrId { get; set; }

    [Option]
    public bool Overwrite { get; set; }
}

[Verb("check")]
class CheckOptions : IdCrudOptions
{

}

[Verb("ocr")]
class OcrOptions : IdCrudOptions
{
    [Option(SetName = "pid")]
    public string Pid { get; set; }

    [Option(SetName = "pidfile")]
    public string PidFile { get; set; }
}

// [Verb("testocr")]
// class TestOcrOptions
// {
//     [Option]
//     public string HocrDirectory { get; set; }

//     [Option]
//     public string OcrDirectory { get; set; }
// }

// [Verb("testupload")]
// class TestUploadOptions
// {
//     [Option]
//     public string JpgDirectory { get; set; }

//     [Option]
//     public int HtrId { get; set; }

//     [Option(Default = false)]
//     public bool Overwrite { get; set; }
// }

// [Verb("testdownload")]
// class TestDownloadOptions
// {
//     [Option]
//     public string HocrDirectory { get; set; }

//     [Option]
//     public string AltoDirectory { get; set; }
// }

// [Verb("testxslt")]
// class TestXsltOptions
// {
//     [Option]
//     public string HocrDirectory { get; set; }

//     [Option]
//     public string AltoDirectory { get; set; }
// }
