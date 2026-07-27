using LiteDb.Distributed.Server.Context;

namespace LiteDb.Distributed.Server.Configuration
{
    public static class ProductionConfigurationValidator
    {
        private const string DevelopmentEnvironmentName = "Development";
        private const string DefaultRootApiKey = "root";
        private const string DefaultReplicationApiKey = "I_AM_ONE_OF_YOU";

        public static void Validate(string environmentName, ClusterNodeOptions nodeOptions, ApiKeyAuthorizationOptions authOptions)
        {
            ArgumentNullException.ThrowIfNull(nodeOptions);
            ArgumentNullException.ThrowIfNull(authOptions);

            if (string.Equals(environmentName, DevelopmentEnvironmentName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            List<string> errors = new List<string>();

            if (string.IsNullOrWhiteSpace(nodeOptions.ReplicationApiKey) || string.Equals(nodeOptions.ReplicationApiKey.Trim(), DefaultReplicationApiKey, StringComparison.Ordinal))
            {
                errors.Add("Node:ReplicationApiKey must be set to a non-default value outside Development.");
            }

            if (string.IsNullOrWhiteSpace(authOptions.RootApiKey) || string.Equals(authOptions.RootApiKey.Trim(), DefaultRootApiKey, StringComparison.Ordinal))
            {
                errors.Add("Auth:RootApiKey must be set to a non-default value outside Development.");
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"Unsafe production configuration: {string.Join(" ", errors)}");
            }
        }
    }
}
