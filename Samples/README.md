# Samples

`SaveFewRecordsSample` is a minimal client that targets the default server URL:

- `http://localhost:1446`

Run it with:

```powershell
dotnet run --project .\Samples\SaveFewRecordsSample\SaveFewRecordsSample.csproj
```

Override the server URL with:

```powershell
$env:DLITEDB_SERVER_URL = "http://localhost:1446"
```
