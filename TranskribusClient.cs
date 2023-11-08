using System.Security;
using System.Xml.Linq;
using Flurl.Http;
using Flurl.Http.Xml;
using Newtonsoft.Json;

class TranskribusClient
{
    private FlurlClient Client { get; set; }
    private string Username { get; set; }
    private string Password { get; set; }
    private AuthResponse AuthResponse { get; set; }
    private DateTimeOffset AuthResponseRetrieved { get; set; }

    private const string ApiUri = "https://transkribus.eu/processing/v1/processes";
    private const string AuthUri = "https://account.readcoop.eu/auth/realms/readcoop/protocol/openid-connect/token";

    public TranskribusClient(string username, string password)
    {
        Username = username;
        Password = password;
        Client = new FlurlClient(ApiUri).BeforeCall(call => Authorize(call.Request));
    }

    private async Task Authorize(IFlurlRequest request)
    {
        if (AuthResponse is null || AuthResponseRetrieved.AddSeconds(AuthResponse.RefreshExpiresIn) < DateTimeOffset.Now.AddMinutes(-5))
        {
            AuthResponse = await AuthUri.PostUrlEncodedAsync(new 
            {
                username = Username,
                password = Password,
                grant_type = "password",
                client_id = "processing-api-client"
            }).ReceiveJson<AuthResponse>();
            AuthResponseRetrieved = DateTimeOffset.Now;
        }
        else if (AuthResponseRetrieved.AddSeconds(AuthResponse.ExpiresIn - 30) < DateTimeOffset.Now)
        {
            AuthResponse = await AuthUri.PostUrlEncodedAsync(new
            {
                refresh_token = AuthResponse.RefreshToken,
                grant_type = "refresh_token",
                client_id = "processing-api-client"
            }).ReceiveJson<AuthResponse>();
            AuthResponseRetrieved = DateTimeOffset.Now;
        }
        request.WithHeader("Authorization", "Bearer " + AuthResponse.AccessToken);
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
        return (int) response.processId;
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

class AuthResponse
{
    [JsonProperty("access_token")]
    public string AccessToken { get; set; }

    [JsonProperty("expires_in")]
    public long ExpiresIn { get; set; }

    [JsonProperty("refresh_expires_in")]
    public long RefreshExpiresIn { get; set; }

    [JsonProperty("refresh_token")]
    public string RefreshToken { get; set; }

    [JsonProperty("token_type")]
    public string TokenType { get; set; }

    [JsonProperty("not-before-policy")]
    public long NotBeforePolicy { get; set; }

    [JsonProperty("session_state")]
    public Guid SessionState { get; set; }

    [JsonProperty("scope")]
    public string Scope { get; set; }
}