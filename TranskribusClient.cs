using System.Xml.Linq;
using Flurl.Http;
using Flurl.Http.Xml;

class TranskribusClient
{
    private FlurlClient Client { get; set; }
    private string AccessToken { get; set; }

    public TranskribusClient()
    {
        Client = new FlurlClient("https://transkribus.eu/processing/v1/processes");
    }

    public async Task Authorize(string username, string password)
    {
        var response = await "https://account.readcoop.eu/auth/realms/readcoop/protocol/openid-connect/token".PostUrlEncodedAsync(new 
        {
            username,
            password,
            grant_type = "password",
            client_id = "processing-api-client"
        }).ReceiveJson();
        AccessToken = response.access_token;
        Client.BeforeCall(call => call.Request.WithHeader("Authorization", "Bearer " + AccessToken));
    }

    public async Task<int> Process(int htrId, string imageBase64)
    {
        var response = await Client.Request().PostJsonAsync(new {
            config = new 
            {
                textRecognition = new
                {
                    htrId
                }
            },
            image = new
            {
                base64 = imageBase64
            }
        }).ReceiveJson();
        return response.processId;
    }

    public async Task<string> GetProcessStatus(int processId)
    {
        var response = await Client.Request(processId).GetJsonAsync();
        return response.status;
    }

    public async Task<XDocument> GetAltoXml(int processId)
    {
        return await Client.Request(processId, "alto").GetXDocumentAsync();
    }
}