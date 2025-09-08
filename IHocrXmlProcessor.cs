using System.Xml.Linq;

interface IHocrXmlProcessor
{
    /// <summary>
    /// Used to set up any initial state, such as creating directories
    /// </summary>
    void Init();
    
    /// <summary>
    /// Receives each hOCR xml tree for processing
    /// </summary>
    /// <param name="hocrFile">Path to the hOCR file</param>
    /// <param name="hocrXml">Xml tree representing the hOCR file</param>
    /// <returns>True if the xml tree was modified and needs to be saved back to the file.</returns>
    bool Process(string hocrFile, XDocument hocrXml);
}
