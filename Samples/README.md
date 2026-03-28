# Samples

`SaveFewRecordsSample` is a minimal client.

It reads configuration from:

- `Samples/SaveFewRecordsSample/sample-settings.json`

Default config:

- `ServerUrl`: `http://localhost:17001`
- `Database`: `testapp`
- `ApiKey`: `sample-local-key`

Run it with:

```powershell
dotnet run --project .\Samples\SaveFewRecordsSample\SaveFewRecordsSample.csproj
```

To point to a different node or database, edit `sample-settings.json`.
