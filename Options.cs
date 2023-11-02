using CommandLine;

abstract class IdCrudOptions
{
    [Option]
    public string Root { get; set; }

    [Option]
    public string Uri { get; set; }
}

[Verb("process", isDefault: true)]
class ProcessOptions : IdCrudOptions
{
    [Option]
    public string Pid { get; set; }

    [Option]
    public int HtrId { get; set; }
}

[Verb("check")]
class CheckOptions : IdCrudOptions
{

}

[Verb("test")]
class TestJpgsOptions
{
    [Option]
    public string JpgDirectory { get; set; }

    [Option]
    public int HtrId { get; set; }
}