using LiteDb.Distributed.Server.Storage.Internal.Schema;
using LiteDB;

namespace LiteDb.Distributed.Server.Storage.Internal.Mapping
{
    internal static class LiteDbDocumentMapper
    {
        public static BsonDocument ParsePayloadAsDocument(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return new BsonDocument();
            }

            BsonValue value = LiteDB.JsonSerializer.Deserialize(payload);

            if (value.IsDocument)
            {
                return value.AsDocument;
            }

            throw new InvalidOperationException("Operation payload must be a JSON document.");
        }

        public static void ReplacePayload(BsonDocument target, BsonDocument payload, string entityId)
        {
            // Keep system metadata untouched and only replace business fields.
            ClearBusinessFields(target);
            target["_id"] = entityId;

            foreach (KeyValuePair<string, BsonValue> entry in payload)
            {
                if (string.Equals(entry.Key, "_id", StringComparison.OrdinalIgnoreCase) || entry.Key.StartsWith("_sys_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                target[entry.Key] = entry.Value;
            }
        }

        public static void ApplySystemMetadata(BsonDocument target, string version, bool isDeleted, bool isTombstone, string lastWriterNodeId, DateTime modifiedUtc)
        {
            target[LiteDbSystemFields.Version] = version;
            target[LiteDbSystemFields.Deleted] = isDeleted;
            target[LiteDbSystemFields.Tombstone] = isTombstone;
            target[LiteDbSystemFields.LastWriterNodeId] = lastWriterNodeId;
            target[LiteDbSystemFields.LastModifiedUtc] = modifiedUtc;
        }

        public static void ClearBusinessFields(BsonDocument document)
        {
            List<string> keysToRemove = document.Keys.Where(key => !string.Equals(key, "_id", StringComparison.Ordinal) && !key.StartsWith("_sys_", StringComparison.Ordinal)).ToList();

            foreach (string key in keysToRemove)
            {
                document.Remove(key);
            }
        }

        public static string SerializePayloadOnly(BsonDocument materialized)
        {
            BsonDocument payload = new BsonDocument();

            foreach (KeyValuePair<string, BsonValue> entry in materialized)
            {
                if (string.Equals(entry.Key, "_id", StringComparison.Ordinal) || entry.Key.StartsWith("_sys_", StringComparison.Ordinal))
                {
                    continue;
                }

                payload[entry.Key] = entry.Value;
            }

            return LiteDB.JsonSerializer.Serialize(payload);
        }

        public static string ReadString(BsonDocument document, string field)
        {
            return document.TryGetValue(field, out BsonValue? value) && value.IsString ? value.AsString : string.Empty;
        }

        public static bool ReadBoolean(BsonDocument document, string field)
        {
            return document.TryGetValue(field, out BsonValue? value) && value.IsBoolean && value.AsBoolean;
        }

        public static DateTime ReadDateTime(BsonDocument document, string field)
        {
            if (!document.TryGetValue(field, out BsonValue? value) || !value.IsDateTime)
            {
                return DateTime.MinValue;
            }

            return DateTime.SpecifyKind(value.AsDateTime, DateTimeKind.Utc);
        }
    }
}
