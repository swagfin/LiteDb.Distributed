namespace LiteDb.Distributed.Server.Infrastructure.Dashboard
{
    internal static class DashboardFileHelper
    {
        public static DashboardFileStatusDto BuildFileStatus(string path)
        {
            bool exists = File.Exists(path);
            if (!exists)
            {
                return new DashboardFileStatusDto
                {
                    Path = path,
                    Exists = false,
                    SizeBytes = 0,
                    LastWriteUtc = null
                };
            }

            FileInfo info = new FileInfo(path);
            return new DashboardFileStatusDto
            {
                Path = info.FullName,
                Exists = true,
                SizeBytes = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc
            };
        }

        public static string ResolveDataDirectory(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            }

            return Path.IsPathRooted(dataDirectory)
                ? Path.GetFullPath(dataDirectory)
                : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dataDirectory));
        }
    }
}
