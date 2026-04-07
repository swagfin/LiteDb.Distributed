IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

//studio project
builder.AddProject<Projects.LiteDb_Distributed_Studio>("studio");

ConfigureNode(
    AddNodeProject(builder, "node-1"),
    "node-1",
    "http://localhost:17001",
    17001,
    ("node-2", "http://localhost:17002"),
    ("node-3", "http://localhost:17003"));

ConfigureNode(
    AddNodeProject(builder, "node-2"),
    "node-2",
    "http://localhost:17002",
    17002,
    ("node-1", "http://localhost:17001"),
    ("node-3", "http://localhost:17003"));

ConfigureNode(
    AddNodeProject(builder, "node-3"),
    "node-3",
    "http://localhost:17003",
    17003,
    ("node-1", "http://localhost:17001"),
    ("node-2", "http://localhost:17002"));



builder.Build().Run();

static IResourceBuilder<ProjectResource> AddNodeProject(IDistributedApplicationBuilder builder, string name)
{
    return builder.AddProject<Projects.LiteDb_Distributed_Server>(name, options =>
    {
        options.ExcludeLaunchProfile = true;
        options.ExcludeKestrelEndpoints = true;
    });
}

static void ConfigureNode(IResourceBuilder<ProjectResource> node, string nodeId, string url, int port, params (string NodeId, string BaseUrl)[] peers)
{
    node.WithEnvironment("urls", url)
        .WithHttpEndpoint(targetPort: port, port: port, name: "http", env: null, isProxied: false)
        .WithEnvironment("Node__NodeId", nodeId);

    for (int index = 0; index < peers.Length; index++)
    {
        (string NodeId, string BaseUrl) peer = peers[index];
        node.WithEnvironment($"Node__SeedPeers__{index}__NodeId", peer.NodeId) .WithEnvironment($"Node__SeedPeers__{index}__BaseUrl", peer.BaseUrl) .WithEnvironment($"Node__SeedPeers__{index}__IsActive", "true");
    }
}
