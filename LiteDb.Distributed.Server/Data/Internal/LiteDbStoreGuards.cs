using LiteDb.Distributed.Server.Core.Exceptions;
using LiteDb.Distributed.Server.Core.Models;
using LiteDb.Distributed.Server.Data.Internal.Mapping;
using LiteDb.Distributed.Server.Data.Internal.Schema;
using LiteDB;

namespace LiteDb.Distributed.Server.Data.Internal
{
    internal static class LiteDbStoreGuards
    {
        public static void ValidateParentVersion(string? parentVersion, BsonDocument? existing, string collection, string entityId)
        {
            if (string.IsNullOrWhiteSpace(parentVersion))
            {
                return;
            }

            string? currentVersion = existing is null ? null : LiteDbDocumentMapper.ReadString(existing, LiteDbSystemFields.Version);

            if (!string.Equals(currentVersion, parentVersion, StringComparison.Ordinal))
            {
                throw new VersionMismatchException($"Version mismatch for {collection}/{entityId}. Expected '{parentVersion}', current is '{currentVersion ?? "<null>"}'.");
            }
        }

        public static void ValidateBusinessCollection(string collection)
        {
            if (string.IsNullOrWhiteSpace(collection))
            {
                throw new ArgumentException("Collection name is required.", nameof(collection));
            }

            if (collection.StartsWith("_sys_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("System collections are internal and cannot be used as business collections.");
            }
        }

        public static void ValidateOperation(OperationRecord operation)
        {
            ArgumentNullException.ThrowIfNull(operation);

            if (string.IsNullOrWhiteSpace(operation.Id))
            {
                throw new ArgumentException("Operation id is required.", nameof(operation));
            }

            if (string.IsNullOrWhiteSpace(operation.NodeId))
            {
                throw new ArgumentException("Operation node id is required.", nameof(operation));
            }

            if (string.IsNullOrWhiteSpace(operation.Collection))
            {
                throw new ArgumentException("Operation collection is required.", nameof(operation));
            }

            if (string.IsNullOrWhiteSpace(operation.EntityId))
            {
                throw new ArgumentException("Operation entity id is required.", nameof(operation));
            }
        }
    }
}
