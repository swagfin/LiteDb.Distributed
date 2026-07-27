using LiteDb.Distributed.Server.Configuration;
using LiteDb.Distributed.Server.Core.Context;

namespace LiteDb.Distributed.Tests
{
    public class ProductionConfigurationValidatorTests
    {
        [Fact]
        public void Validate_AllowsDevelopmentDefaults()
        {
            ClusterNodeOptions nodeOptions = new ClusterNodeOptions { NodeId = "node-a" };
            ApiKeyAuthorizationOptions authOptions = new ApiKeyAuthorizationOptions();

            ProductionConfigurationValidator.Validate("Development", nodeOptions, authOptions);
        }

        [Fact]
        public void Validate_RejectsProductionDefaults()
        {
            ClusterNodeOptions nodeOptions = new ClusterNodeOptions { NodeId = "node-a" };
            ApiKeyAuthorizationOptions authOptions = new ApiKeyAuthorizationOptions();

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ProductionConfigurationValidator.Validate("Production", nodeOptions, authOptions));

            Assert.Contains("Node:ReplicationApiKey", exception.Message);
            Assert.Contains("Auth:RootApiKey", exception.Message);
        }

        [Fact]
        public void Validate_AllowsProductionWithExplicitSecrets()
        {
            ClusterNodeOptions nodeOptions = new ClusterNodeOptions
            {
                NodeId = "node-a",
                ReplicationApiKey = "prod-replication-key"
            };
            ApiKeyAuthorizationOptions authOptions = new ApiKeyAuthorizationOptions
            {
                RootApiKey = "prod-root-key"
            };

            ProductionConfigurationValidator.Validate("Production", nodeOptions, authOptions);
        }
    }
}
