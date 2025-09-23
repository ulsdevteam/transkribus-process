using System.Text.RegularExpressions;
using System.Xml.Linq;

partial class WordAlignmentFixer : IHocrXmlProcessor
{
    public void Init()
    {
        Console.WriteLine("Fixing horizontal word alignment...");
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
                var lineBbox = BboxRegex().Match(lineAttributes).Groups
                    .Cast<Group>()
                    .Skip(1)
                    .Select(g => int.Parse(g.Value))
                    .ToArray();
                var words = line.Elements(ns + "span");
                // # of characters = sum of word lengths + the spaces between
                var lineCharLength = words.Sum(w => w.Value.Length) + words.Count() - 1;
                var linePixLength = lineBbox[2] - lineBbox[0];
                var pixPerChar = linePixLength / lineCharLength;
                var currentPosition = lineBbox[0];
                foreach (var word in words)
                {
                    var newLeft = currentPosition;
                    var newRight = currentPosition += word.Value.Length * pixPerChar;
                    var newBbox = $"bbox {newLeft} {lineBbox[1]} {newRight} {lineBbox[3]}";
                    word.Attribute("title").SetValue(BboxRegex().Replace(word.Attribute("title").Value, newBbox));
                    currentPosition += pixPerChar;
                }
            }
        }
        return true;
    }

    [GeneratedRegex(@"bbox (\d+) (\d+) (\d+) (\d+)")]
    private static partial Regex BboxRegex();
}