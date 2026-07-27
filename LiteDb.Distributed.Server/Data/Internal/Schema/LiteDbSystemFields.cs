namespace LiteDb.Distributed.Server.Data.Internal.Schema
{
    internal static class LiteDbSystemFields
    {
        public const string Version = "_sys_version";
        public const string Deleted = "_sys_deleted";
        public const string Tombstone = "_sys_tombstone";
        public const string LastWriterNodeId = "_sys_last_writer_node_id";
        public const string LastModifiedUtc = "_sys_last_modified_utc";
    }
}
