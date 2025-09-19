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
                var words = line.Elements(ns + "span").ToList();
                for (int i = 0; i < words.Count - 1; i++)
                {
                    var leftWord = words[i];
                    var rightWord = words[i + 1];
                    var leftBbox = leftWord.Attribute("title").Value.Split(';')
                        .Select(x => x.Trim())
                        .First(x => x.StartsWith("bbox"))
                        .Split(' ');
                    var rightBbox = rightWord.Attribute("title").Value.Split(';')
                        .Select(x => x.Trim())
                        .First(x => x.StartsWith("bbox"))
                        .Split(' ');
                    var leftEnd = int.Parse(leftBbox[3]);
                    var rightStart = int.Parse(rightBbox[1]);
                    if (leftEnd > rightStart)
                    {
                        leftBbox[3] = rightStart.ToString();
                        rightBbox[1] = leftEnd.ToString();
                        leftWord.Attribute("title").SetValue(string.Join(' ', leftBbox));
                        rightWord.Attribute("title").SetValue(string.Join(' ', rightBbox));
                    }
                }

                // var lineAttributes = line.Attribute("title").Value;
                // var bbox = lineAttributes.Split(';')
                //     .Select(x => x.Trim())
                //     .First(x => x.StartsWith("bbox"))
                //     .Split(' ');
                // var pixelHeight = int.Parse(bbox[4]) - int.Parse(bbox[2]);
                // var fontSize = Math.Floor(pixelHeight * PixelToFontSizeConversionFactor);
                // line.Attribute("title").SetValue(lineAttributes + $"; x_fsize {fontSize}");
            }
        }
        return true;
    }
}