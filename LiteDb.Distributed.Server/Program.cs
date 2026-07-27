using LiteDb.Distributed.Server;
using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Core.Context;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:1446");
string[] studioCorsOrigins = builder.Configuration.GetSection("Studio:CorsOrigins").Get<string[]>() ?? Array.Empty<string>();

builder.Services.AddControllers().AddJsonOptions(options => { options.JsonSerializerOptions.PropertyNamingPolicy = null; });

builder.Services.ConfigureHttpJsonOptions(options => { options.SerializerOptions.PropertyNamingPolicy = null; });

builder.Services.AddCors(options =>
{
    options.AddPolicy("StudioCors", policy =>
    {
        if (studioCorsOrigins.Length > 0)
        {
            policy.WithOrigins(studioCorsOrigins);
        }
        else
        {
            policy.SetIsOriginAllowed(origin =>
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
                {
                    return false;
                }

                return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
            });
        }

        policy.AllowAnyHeader();
        policy.AllowAnyMethod();
    });
});

ClusterNodeOptions nodeOptions = new ClusterNodeOptions();
builder.Configuration.GetSection("Node").Bind(nodeOptions);

ApiKeyAuthorizationOptions authOptions = new ApiKeyAuthorizationOptions();
builder.Configuration.GetSection("Auth").Bind(authOptions);
ProductionConfigurationValidator.Validate(builder.Environment.EnvironmentName, nodeOptions, authOptions);

builder.Services.AddLiteDbDistributedNode(nodeOptions);
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton<IApiKeyAuthorizationService, ApiKeyAuthorizationService>();

WebApplication app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("StudioCors");

app.MapControllers();

app.Run();
