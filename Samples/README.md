# Samples

`SaveFewRecordsSample` is a minimal client that targets the default server URL:

- `http://localhost:1446`

The sample sends required headers:

- `Database`
- `ApiKey`

Run it with:

```powershell
dotnet run --project .\Samples\SaveFewRecordsSample\SaveFewRecordsSample.csproj
```

Override the server URL with:

```powershell
$env:DLITEDB_SERVER_URL = "http://localhost:1446"
```

You can also override logical database and key:

```powershell
$env:DLITEDB_DATABASE = "testapp"
$env:DLITEDB_API_KEY = "sample-local-key"
```
