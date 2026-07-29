using System.Text.Json;
using LiteDb.Distributed.Server.Core.Common;

namespace LiteDb.Distributed.Server.Infrastructure.Documents
{
    internal static class DocumentPayloadNormalizer
    {
        public static bool TryExtractEntityId(JsonElement payload, out string entityId)
        {
            entityId = string.Empty;

            if (payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return TryReadPropertyAsString(payload, "Id", out entityId);
        }

        public static bool TryNormalizeUpsertPayload(JsonElement payload, string routeId, out JsonElement normalizedPayload, out string error)
        {
            normalizedPayload = default;
            error = string.Empty;

            if (payload.ValueKind != JsonValueKind.Object)
            {
                error = "PUT body must be a JSON object.";
                return false;
            }

            if (CanUsePayloadAsIs(payload, routeId))
            {
                normalizedPayload = payload;
                return true;
            }

            using MemoryStream stream = new MemoryStream();
            using Utf8JsonWriter writer = new Utf8JsonWriter(stream);

            // Route id is source-of-truth for upsert identity; body Id/_id is overwritten.
            writer.WriteStartObject();
            foreach (JsonProperty property in payload.EnumerateObject())
            {
                if (IsReservedPayloadField(property.Name))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteString("Id", routeId);
            writer.WriteEndObject();
            writer.Flush();

            stream.Position = 0;
            using JsonDocument document = JsonDocument.Parse(stream);
            normalizedPayload = document.RootElement.Clone();
            return true;
        }

        public static bool IsReservedCollection(string? collectionName)
        {
            return string.Equals(collectionName, Common.CacheCollectionName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadPropertyAsString(JsonElement payload, string propertyName, out string value)
        {
            value = string.Empty;

            if (!payload.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? candidate = property.GetString();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            value = candidate;
            return true;
        }

        private static bool CanUsePayloadAsIs(JsonElement payload, string routeId)
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            bool hasId = false;

            foreach (JsonProperty property in payload.EnumerateObject())
            {
                if (string.Equals(property.Name, Common.InternalIdField, StringComparison.OrdinalIgnoreCase) || property.Name.StartsWith(Common.SystemPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!string.Equals(property.Name, Common.IdField, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                string? id = property.Value.GetString();
                if (!string.Equals(id, routeId, StringComparison.Ordinal))
                {
                    return false;
                }

                hasId = true;
            }

            return hasId;
        }

        private static bool IsReservedPayloadField(string propertyName)
        {
            return string.Equals(propertyName, Common.IdField, StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, Common.InternalIdField, StringComparison.OrdinalIgnoreCase)
                || propertyName.StartsWith(Common.SystemPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
