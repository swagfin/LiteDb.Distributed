using System.Text.Json;
using System.Text.RegularExpressions;
using LiteDb.Distributed.Studio.Models;
using LiteDb.Distributed.Studio.Services;
using Microsoft.AspNetCore.Components;

namespace LiteDb.Distributed.Studio.Pages
{
    public partial class Home : ComponentBase
    {
        [Inject]
        public required ProfileStore ProfileStore { get; init; }

        [Inject]
        public required DistributedApiClient ApiClient { get; init; }

        private static readonly Regex DatabaseNamePattern = new("^[a-z0-9][a-z0-9_-]{0,62}$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions PrettyJsonOptions = new()
        {
            WriteIndented = true
        };

        private List<ConnectionProfile> _profiles = [];
        private ConnectionProfile _editor = ConnectionProfile.CreateDefault();

        private Guid? _selectedProfileId;
        private Guid? _activeProfileId;

        private DashboardOverviewDto? _overview;
        private List<string> _collections = [];
        private string? _selectedCollection;
        private string _collectionInput = string.Empty;

        private int _skip;
        private int _take = 100;
        private string _idLookup = string.Empty;

        private int _queryTake = 200;
        private string _queryText = "SELECT $ FROM OrderTransactions LIMIT 200";

        private List<Dictionary<string, JsonElement>> _documents = [];
        private Dictionary<string, JsonElement>? _selectedDocument;

        private string _selectedDocumentId = string.Empty;
        private string _documentJson = "{\n  \"Id\": \"\"\n}";

        private bool _busy;
        private bool _savingProfile;
        private bool _connectingProfile;
        private bool _showProfileManagement = true;
        private string? _errorMessage;
        private string? _infoMessage;

        private ConnectionProfile? ActiveProfile => _activeProfileId is null ? null : _profiles.FirstOrDefault(x => x.Id == _activeProfileId.Value);

        private ConnectionProfile? SelectedProfile => _selectedProfileId is null ? null : _profiles.FirstOrDefault(x => x.Id == _selectedProfileId.Value);

        private IReadOnlyList<ConnectionProfile> OrderedProfiles => _profiles.OrderByDescending(x => x.UpdatedUtc).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

        private bool ShowProfileManagement => _showProfileManagement || ActiveProfile is null;

        private bool IsProfileActionBusy => _savingProfile || _connectingProfile;

        private string ActiveProfileSummary => ActiveProfile is null ? "Not connected. Open profile management to connect." : $"Connected to {ActiveProfile.Database} at {ActiveProfile.BaseUrl}";

        private IReadOnlyList<string> DisplayColumns
        {
            get
            {
                List<string> keys = _documents.SelectMany(x => x.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

                int idIndex = keys.FindIndex(x => string.Equals(x, "Id", StringComparison.OrdinalIgnoreCase));
                if (idIndex > 0)
                {
                    string id = keys[idIndex];
                    keys.RemoveAt(idIndex);
                    keys.Insert(0, id);
                }

                if (keys.Count == 0)
                {
                    keys.Add("Result");
                }

                return keys;
            }
        }

        protected override async Task OnInitializedAsync()
        {
            await LoadProfilesAsync().ConfigureAwait(false);
        }

        private async Task LoadProfilesAsync()
        {
            _busy = true;
            ClearMessages();

            try
            {
                IReadOnlyList<ConnectionProfile> loaded = await ProfileStore.LoadProfilesAsync().ConfigureAwait(false);

                _profiles = loaded.OrderByDescending(x => x.UpdatedUtc).ToList();
                Guid? savedActiveProfileId = await ProfileStore.LoadActiveProfileIdAsync().ConfigureAwait(false);
                _activeProfileId = null;
                _showProfileManagement = true;

                if (_profiles.Count == 0)
                {
                    _selectedProfileId = null;
                    _editor = ConnectionProfile.CreateDefault();
                    _infoMessage = "Create a profile and connect to open Data Explorer.";
                    return;
                }

                ConnectionProfile starterProfile = _profiles.FirstOrDefault(x => x.Id == savedActiveProfileId)
                                     ?? _profiles[0];

                _selectedProfileId = starterProfile.Id;
                _editor = starterProfile.Clone();
                _infoMessage = "Select a profile and click Connect to open Data Explorer.";
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to load profiles: {ex.Message}";
            }
            finally
            {
                _busy = false;
            }
        }

        private void SelectProfile(Guid profileId)
        {
            ConnectionProfile? profile = _profiles.FirstOrDefault(x => x.Id == profileId);
            if (profile is null)
            {
                return;
            }

            _selectedProfileId = profile.Id;
            _editor = profile.Clone();
            ClearMessages();
        }

        private void StartNewProfile()
        {
            _selectedProfileId = null;
            _editor = ConnectionProfile.CreateDefault();
            ClearMessages();
        }

        private void OpenProfileManagement()
        {
            _showProfileManagement = true;
            ClearMessages();
        }

        private void OpenDataExplorer()
        {
            if (ActiveProfile is null)
            {
                _showProfileManagement = true;
                return;
            }

            _showProfileManagement = false;
            ClearMessages();
        }

        private async Task SaveProfileAsync()
        {
            if (!TryNormalizeProfile(_editor, out ConnectionProfile? normalized, out string? error))
            {
                _errorMessage = error;
                _infoMessage = null;
                return;
            }

            _savingProfile = true;
            try
            {
                UpsertProfile(normalized);

                _selectedProfileId = normalized.Id;
                _editor = normalized.Clone();

                await PersistProfilesAsync().ConfigureAwait(false);

                _infoMessage = $"Profile '{normalized.Name}' saved.";
                _errorMessage = null;
            }
            finally
            {
                _savingProfile = false;
            }
        }

        private async Task DeleteProfileAsync()
        {
            if (_selectedProfileId is null)
            {
                return;
            }

            int removed = _profiles.RemoveAll(x => x.Id == _selectedProfileId.Value);
            if (removed == 0)
            {
                return;
            }

            if (_activeProfileId == _selectedProfileId)
            {
                _activeProfileId = null;
                _showProfileManagement = true;
                _overview = null;
                _collections = [];
                _selectedCollection = null;
                _collectionInput = string.Empty;
                _documents = [];
                _selectedDocument = null;
                _selectedDocumentId = string.Empty;
                CreateDocumentTemplate();
                await ProfileStore.SaveActiveProfileIdAsync(null).ConfigureAwait(false);
            }

            _selectedProfileId = _profiles.FirstOrDefault()?.Id;
            _editor = _selectedProfileId is Guid nextId ? _profiles.First(x => x.Id == nextId).Clone() : ConnectionProfile.CreateDefault();

            await PersistProfilesAsync().ConfigureAwait(false);

            _errorMessage = null;
            _infoMessage = "Profile deleted.";
        }

        private async Task ConnectUsingEditorAsync()
        {
            if (!TryNormalizeProfile(_editor, out ConnectionProfile? normalized, out string? error))
            {
                _errorMessage = error;
                _infoMessage = null;
                return;
            }

            _connectingProfile = true;
            try
            {
                UpsertProfile(normalized);
                _selectedProfileId = normalized.Id;
                _editor = normalized.Clone();

                await PersistProfilesAsync().ConfigureAwait(false);

                bool connected = await ConnectAsync(normalized, quiet: false).ConfigureAwait(false);
                _showProfileManagement = !connected;
            }
            finally
            {
                _connectingProfile = false;
            }
        }

        private async Task RefreshCollectionsAsync()
        {
            ConnectionProfile? profile = ActiveProfile ?? SelectedProfile;
            if (profile is null)
            {
                _errorMessage = "Pick or create a profile first.";
                _infoMessage = null;
                return;
            }

            bool connected = await ConnectAsync(profile, quiet: true).ConfigureAwait(false);
            if (connected)
            {
                _showProfileManagement = false;
            }
        }

        private async Task<bool> ConnectAsync(ConnectionProfile profile, bool quiet)
        {
            _busy = true;
            ClearMessages();

            try
            {
                ApiResult<DashboardOverviewDto> overviewResult = await ApiClient.GetOverviewAsync(profile.BaseUrl).ConfigureAwait(false);
                if (!overviewResult.Success)
                {
                    _errorMessage = overviewResult.ErrorMessage;
                    return false;
                }

                _overview = overviewResult.Data;

                ApiResult<List<string>> collectionsResult = await ApiClient.GetCollectionsAsync(profile).ConfigureAwait(false);
                if (!collectionsResult.Success)
                {
                    _errorMessage = collectionsResult.ErrorMessage;
                    return false;
                }

                _activeProfileId = profile.Id;
                await ProfileStore.SaveActiveProfileIdAsync(profile.Id).ConfigureAwait(false);
                _collections = collectionsResult.Data ?? [];

                if (_collections.Count == 0)
                {
                    _documents = [];
                    _selectedDocument = null;
                    _selectedCollection = string.IsNullOrWhiteSpace(_collectionInput) ? null : _collectionInput.Trim();

                    if (!quiet)
                    {
                        _infoMessage = "Connected. No collections discovered yet for this database.";
                    }

                    return true;
                }

                if (string.IsNullOrWhiteSpace(_selectedCollection)
                    || !_collections.Contains(_selectedCollection, StringComparer.OrdinalIgnoreCase))
                {
                    _selectedCollection = _collections[0];
                }

                _collectionInput = _selectedCollection;
                UseCollectionTemplate();

                await BrowseCollectionAsync().ConfigureAwait(false);

                if (!quiet)
                {
                    _infoMessage = "Connected and collections loaded.";
                }

                return true;
            }
            catch (Exception ex)
            {
                _errorMessage = $"Failed to connect profile: {ex.Message}";
                return false;
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task UseCollectionInputAsync()
        {
            if (string.IsNullOrWhiteSpace(_collectionInput))
            {
                _errorMessage = "Collection name cannot be empty.";
                _infoMessage = null;
                return;
            }

            _selectedCollection = _collectionInput.Trim();

            if (!_collections.Contains(_selectedCollection, StringComparer.OrdinalIgnoreCase))
            {
                _collections.Add(_selectedCollection);
                _collections = _collections.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            }

            UseCollectionTemplate();
            await BrowseCollectionAsync().ConfigureAwait(false);
        }

        private async Task BrowseCollectionAsync()
        {
            ConnectionProfile? profile = ActiveProfile;
            if (profile is null)
            {
                _errorMessage = "Connect a profile first.";
                _infoMessage = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedCollection))
            {
                _errorMessage = "Select or enter a collection before querying.";
                _infoMessage = null;
                return;
            }

            _busy = true;
            ClearMessages();

            try
            {
                ApiResult<List<Dictionary<string, JsonElement>>> result = await ApiClient.ListDocumentsAsync(profile, _selectedCollection, _skip, _take).ConfigureAwait(false);

                if (!result.Success)
                {
                    _errorMessage = result.ErrorMessage;
                    _documents = [];
                    return;
                }

                _documents = result.Data ?? [];

                if (_documents.Count == 0)
                {
                    _selectedDocument = null;
                    CreateDocumentTemplate();
                    _infoMessage = "Query returned no documents.";
                    return;
                }

                if (!TrySelectDocumentById(_selectedDocumentId))
                {
                    SelectDocument(_documents[0]);
                }

                _infoMessage = $"Loaded {_documents.Count} document(s).";
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task RunLiteQueryAsync()
        {
            ConnectionProfile? profile = ActiveProfile;
            if (profile is null)
            {
                _errorMessage = "Connect a profile first.";
                _infoMessage = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(_queryText))
            {
                _errorMessage = "Enter a query first.";
                _infoMessage = null;
                return;
            }

            _busy = true;
            ClearMessages();

            try
            {
                ApiResult<QueryResponseDto> result = await ApiClient.ExecuteQueryAsync(profile, _queryText, _queryTake).ConfigureAwait(false);

                if (!result.Success)
                {
                    _errorMessage = result.ErrorMessage;
                    _documents = [];
                    return;
                }

                List<Dictionary<string, JsonElement>> rows = result.Data?.Rows ?? [];
                _documents = rows;

                if (_documents.Count > 0)
                {
                    SelectDocument(_documents[0]);
                }
                else
                {
                    _selectedDocument = null;
                    CreateDocumentTemplate();
                }

                _infoMessage = $"LiteQL returned {result.Data?.ReturnedRows ?? 0} row(s).";
            }
            finally
            {
                _busy = false;
            }
        }

        private void UseCollectionTemplate()
        {
            if (string.IsNullOrWhiteSpace(_selectedCollection))
            {
                return;
            }

            _queryText = $"SELECT $ FROM {_selectedCollection} LIMIT {_queryTake}";
        }

        private async Task LookupByIdAsync()
        {
            ConnectionProfile? profile = ActiveProfile;
            if (profile is null)
            {
                _errorMessage = "Connect a profile first.";
                _infoMessage = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedCollection))
            {
                _errorMessage = "Select or enter a collection first.";
                _infoMessage = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(_idLookup))
            {
                _errorMessage = "Enter an Id to look up.";
                _infoMessage = null;
                return;
            }

            _busy = true;
            ClearMessages();

            try
            {
                ApiResult<Dictionary<string, JsonElement>> result = await ApiClient.GetDocumentByIdAsync(profile, _selectedCollection, _idLookup.Trim()).ConfigureAwait(false);

                if (!result.Success || result.Data is null)
                {
                    _errorMessage = result.ErrorMessage ?? "Document not found.";
                    _documents = [];
                    return;
                }

                _documents = [result.Data];
                SelectDocument(result.Data);
                _infoMessage = "Document loaded by Id.";
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task ClearIdFilterAsync()
        {
            _idLookup = string.Empty;
            await BrowseCollectionAsync().ConfigureAwait(false);
        }

        private void SelectDocument(Dictionary<string, JsonElement> document)
        {
            _selectedDocument = document;
            _documentJson = JsonSerializer.Serialize(document, PrettyJsonOptions);

            _selectedDocumentId = ExtractId(document) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_selectedDocumentId))
            {
                _idLookup = _selectedDocumentId;
            }
        }

        private async Task SaveDocumentAsync()
        {
            ConnectionProfile? profile = ActiveProfile;
            if (profile is null)
            {
                _errorMessage = "Connect a profile first.";
                _infoMessage = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedCollection))
            {
                _errorMessage = "Select or enter a collection first.";
                _infoMessage = null;
                return;
            }

            string documentId;

            try
            {
                using JsonDocument parsed = JsonDocument.Parse(_documentJson);

                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                {
                    _errorMessage = "Document payload must be a JSON object.";
                    _infoMessage = null;
                    return;
                }

                documentId = string.IsNullOrWhiteSpace(_selectedDocumentId) ? ExtractId(parsed.RootElement) ?? string.Empty : _selectedDocumentId.Trim();

                if (string.IsNullOrWhiteSpace(documentId))
                {
                    _errorMessage = "Document Id is required. Set the Id field or provide it in the Id input.";
                    _infoMessage = null;
                    return;
                }
            }
            catch (JsonException ex)
            {
                _errorMessage = $"JSON parse error: {ex.Message}";
                _infoMessage = null;
                return;
            }

            _busy = true;
            ClearMessages();

            try
            {
                ApiResult<WriteResultDto> result = await ApiClient.PutDocumentAsync(profile, _selectedCollection, documentId, _documentJson).ConfigureAwait(false);

                if (!result.Success)
                {
                    _errorMessage = result.ErrorMessage;
                    return;
                }

                _selectedDocumentId = documentId;
                _idLookup = documentId;

                await BrowseCollectionAsync().ConfigureAwait(false);
                TrySelectDocumentById(documentId);

                _infoMessage = result.Data is null ? $"Document '{documentId}' saved." : $"Document '{documentId}' saved. Version: {result.Data.Version}";
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task DeleteDocumentAsync()
        {
            ConnectionProfile? profile = ActiveProfile;
            if (profile is null)
            {
                _errorMessage = "Connect a profile first.";
                _infoMessage = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedCollection))
            {
                _errorMessage = "Select or enter a collection first.";
                _infoMessage = null;
                return;
            }

            string documentId = _selectedDocumentId.Trim();
            if (string.IsNullOrWhiteSpace(documentId))
            {
                _errorMessage = "Select a document or provide an Id before deleting.";
                _infoMessage = null;
                return;
            }

            _busy = true;
            ClearMessages();

            try
            {
                ApiResult<WriteResultDto> result = await ApiClient.DeleteDocumentAsync(profile, _selectedCollection, documentId).ConfigureAwait(false);

                if (!result.Success)
                {
                    _errorMessage = result.ErrorMessage;
                    return;
                }

                _selectedDocumentId = string.Empty;
                _idLookup = string.Empty;
                CreateDocumentTemplate();

                await BrowseCollectionAsync().ConfigureAwait(false);

                _infoMessage = $"Document '{documentId}' deleted.";
            }
            finally
            {
                _busy = false;
            }
        }

        private void CreateDocumentTemplate()
        {
            _selectedDocument = null;
            _selectedDocumentId = string.Empty;
            _documentJson = "{\n  \"Id\": \"\",\n  \"CreatedUtc\": \"" + DateTime.UtcNow.ToString("O") + "\"\n}";
        }

        private string GetDocumentRowClass(Dictionary<string, JsonElement> document)
        {
            string? id = ExtractId(document);

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(_selectedDocumentId))
            {
                return string.Empty;
            }

            return string.Equals(id, _selectedDocumentId, StringComparison.Ordinal) ? "selected" : string.Empty;
        }

        private static string FormatCell(Dictionary<string, JsonElement> document, string column)
        {
            if (!TryGetValueIgnoreCase(document, column, out JsonElement value))
            {
                return string.Empty;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => "null",
                JsonValueKind.Undefined => string.Empty,
                JsonValueKind.Object => TrimCell(value.GetRawText()),
                JsonValueKind.Array => TrimCell(value.GetRawText()),
                _ => TrimCell(value.GetRawText())
            };
        }

        private static string TrimCell(string raw)
        {
            const int maxLength = 84;

            return raw.Length <= maxLength ? raw : raw[..maxLength] + "...";
        }

        private static string? ExtractId(Dictionary<string, JsonElement> document)
        {
            if (TryGetValueIgnoreCase(document, "Id", out JsonElement value)
                && TryReadIdValue(value, out string? id))
            {
                return id;
            }

            if (TryGetValueIgnoreCase(document, "_id", out JsonElement internalId)
                && TryReadIdValue(internalId, out string? normalizedInternalId))
            {
                return normalizedInternalId;
            }

            return null;
        }

        private static string? ExtractId(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(property.Name, "_id", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryReadIdValue(property.Value, out string? id))
                {
                    return id;
                }
            }

            return null;
        }

        private bool TrySelectDocumentById(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            Dictionary<string, JsonElement>? document = _documents.FirstOrDefault(x => string.Equals(ExtractId(x), id, StringComparison.Ordinal));
            if (document is null)
            {
                return false;
            }

            SelectDocument(document);
            return true;
        }

        private static bool TryReadIdValue(JsonElement element, out string id)
        {
            id = string.Empty;

            if (element.ValueKind == JsonValueKind.String)
            {
                string? candidate = element.GetString();
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return false;
                }

                id = candidate.Trim();
                return true;
            }

            if (element.ValueKind == JsonValueKind.Number)
            {
                id = element.GetRawText();
                return true;
            }

            return false;
        }

        private static bool TryGetValueIgnoreCase(IReadOnlyDictionary<string, JsonElement> dictionary, string key, out JsonElement value)
        {
            foreach (KeyValuePair<string, JsonElement> entry in dictionary)
            {
                if (!string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = entry.Value;
                return true;
            }

            value = default;
            return false;
        }

        private static string MaskApiKey(string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return "-";
            }

            string normalized = apiKey.Trim();
            if (normalized.Length <= 6)
            {
                return new string('*', normalized.Length);
            }

            return $"{normalized[..3]}***{normalized[^3..]}";
        }

        private void UpsertProfile(ConnectionProfile profile)
        {
            int index = _profiles.FindIndex(x => x.Id == profile.Id);

            if (index >= 0)
            {
                _profiles[index] = profile;
                return;
            }

            _profiles.Add(profile);
        }

        private async Task PersistProfilesAsync()
        {
            _profiles = _profiles.OrderByDescending(x => x.UpdatedUtc).ToList();

            await ProfileStore.SaveProfilesAsync(_profiles).ConfigureAwait(false);
        }

        private static bool TryNormalizeProfile(ConnectionProfile input, out ConnectionProfile profile, out string? error)
        {
            string baseUrl = (input.BaseUrl ?? string.Empty).Trim();
            string database = (input.Database ?? string.Empty).Trim().ToLowerInvariant();
            string credential = (input.Credential ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                error = "Server URL is required.";
                profile = input;
                return false;
            }

            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = "http://" + baseUrl;
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? serverUri)
                || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            {
                error = "Server URL must be a valid http or https URL.";
                profile = input;
                return false;
            }

            if (string.IsNullOrWhiteSpace(database))
            {
                error = "Database is required.";
                profile = input;
                return false;
            }

            if (!DatabaseNamePattern.IsMatch(database))
            {
                error = "Database name can only include lowercase letters, digits, '-' or '_' (max 63 chars).";
                profile = input;
                return false;
            }

            if (string.IsNullOrWhiteSpace(credential))
            {
                error = "ApiKey is required.";
                profile = input;
                return false;
            }

            string name = (input.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"{database}@{serverUri.Host}";
            }

            profile = input.Clone();
            profile.Name = name;
            profile.BaseUrl = serverUri.ToString().TrimEnd('/');
            profile.Database = database;
            profile.Credential = credential;
            profile.UpdatedUtc = DateTime.UtcNow;

            error = null;
            return true;
        }

        private void ClearMessages()
        {
            _errorMessage = null;
            _infoMessage = null;
        }
    }

}
