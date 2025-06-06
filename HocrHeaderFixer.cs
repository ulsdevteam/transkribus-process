using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

class HocrHeaderFixer : IHocrXmlProcessor
{
    public int? HtrId { get; set; }
    public string FileName { get; set; }

    public HocrHeaderFixer(int? htrId = null, string fileName = null)
    {
        HtrId = htrId;
        FileName = fileName;
    }

    public void Init()
    {
        Console.WriteLine("Fixing hOCR file headers...");
    }

    public void Process(string hocrFile, XDocument hocrXml)
    {
        XNamespace ns = "http://www.w3.org/1999/xhtml";
        var head = hocrXml.Element(ns + "html").Element(ns + "head");
        head.Element(ns + "title").Value = "Image: " + FileName ?? Regex.Replace(Path.GetFileName(hocrFile), "_HOCR.shtml$", "_JP2.jpg");
        head.Add(new XElement(ns + "meta", new XAttribute("name", "ocr-system"), new XAttribute("content", HtrId.HasValue ? $"Transkribus-HtrId:{HtrId}" : "Transkribus")));
        using var fileStream = File.Open(hocrFile, FileMode.Truncate);
        var writer = XmlWriter.Create(fileStream, new XmlWriterSettings
        {
            // need to specify false here to stop it from emitting a byte order mark
            Encoding = new UTF8Encoding(false),
            Indent = true
        });
        hocrXml.Save(writer);
        writer.Close();
    }
}