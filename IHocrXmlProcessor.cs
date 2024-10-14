using System.Xml.Linq;

interface IHocrXmlProcessor
{
    void Init();
    void Process(string hocrFile, XDocument hocrXml);
}
