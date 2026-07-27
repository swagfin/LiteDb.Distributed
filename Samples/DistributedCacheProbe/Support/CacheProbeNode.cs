namespace DistributedCacheProbe.Support
{
    public class CacheProbeNode
    {
        public CacheProbeNode(string name, string baseUrl, HttpClient client)
        {
            Name = name;
            BaseUrl = baseUrl;
            Client = client;
        }

        public string Name { get; set; }
        public string BaseUrl { get; set; }
        public HttpClient Client { get; set; }
    }
}
