using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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
