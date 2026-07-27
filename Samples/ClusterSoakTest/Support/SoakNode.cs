namespace ClusterSoakTest.Support
{
    public class SoakNode
    {
        public SoakNode(string name, string baseUrl, HttpClient client)
        {
            Name = name;
            BaseUrl = baseUrl;
            Client = client;
        }

        public string Name { get; }
        public string BaseUrl { get; }
        public HttpClient Client { get; }
    }
}
