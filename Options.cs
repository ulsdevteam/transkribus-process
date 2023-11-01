using CommandLine;

class Options
{
    [Option]
    public string Root { get; set; }

    [Option]
    public string Uri { get; set; }

    [Option]
    public string Pid { get; set; }

    [Option]
    public int HtrId { get; set; }
}