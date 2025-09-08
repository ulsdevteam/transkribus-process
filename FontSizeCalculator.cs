using System.Xml.Linq;

class FontSizeCalculator(double pixelToFontSizeConversionFactor) : IHocrXmlProcessor
{
    public double PixelToFontSizeConversionFactor { get; set; } = pixelToFontSizeConversionFactor;

    public void Init()
    {
    }

    public bool Process(string hocrFile, XDocument hocrXml)
    {
        XNamespace ns = "http://www.w3.org/1999/xhtml";
        foreach (var paragraph in hocrXml.Descendants(ns + "p"))
        {
            var lines = paragraph.Elements(ns + "span").Where(span => span.Attribute("class")?.Value == "ocr_line");
            foreach (var line in lines)
            {
                var lineAttributes = line.Attribute("title").Value;
                var bbox = lineAttributes.Split(';')
                    .Select(x => x.Trim())
                    .Where(x => x.StartsWith("bbox"))
                    .First()
                    .Split(' ');
                var pixelHeight = int.Parse(bbox[4]) - int.Parse(bbox[2]);
                var fontSize = Math.Floor(pixelHeight * PixelToFontSizeConversionFactor);
                line.Attribute("title").SetValue(lineAttributes + $"; x_fsize {fontSize}");
            }
        }
        return true;
    }
}