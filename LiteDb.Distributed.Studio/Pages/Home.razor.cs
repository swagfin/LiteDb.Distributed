using System.Text.Json;
using System.Text.RegularExpressions;
using LiteDb.Distributed.Studio.Models;
using LiteDb.Distributed.Studio.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace LiteDb.Distributed.Studio.Pages
{
    public partial class Home : ComponentBase
    {
        private const int DefaultBrowseTake = 100;

        [Inject]
        public required ProfileStore ProfileStore { get; init; }

        [Inject]
        public required DistributedApiClient ApiClient { get; init; }

        private static readonly Regex DatabaseNamePattern = new("^[a-z0-9][a-z0-9_-]{0,62}$", RegexOptions.Compiled);
        private static readonly HashSet<string> ReservedCollections = new(StringComparer.OrdinalIgnoreCase) { "cache" };

        private static readonly JsonSerializerOptions PrettyJsonOptions = new()
        {
            WriteIndented = true
        };

        private List<ConnectionProfile> _profiles = [];
        private ConnectionProfile _editor = ConnectionProfile.CreateDefault();

        private Guid? _selectedProfileId;
        private Guid? _activeProfileId;

        private DashboardOverviewDto? _overview;
        private List<string> _discoveredCollections = [];
        private List<string> _collections = [];
        private string? _selectedCollection;
        private string _newCollectionName = string.Empty;
        private bool _creatingCollection;
        private bool _showTableModal;
        private bool _includeSystemCollections;

        private string _queryText = "SELECT $ FROM OrderTransactions LIMIT 100";

        private List<Dictionary<string, JsonElement>> _documents = [];
        private List<string> _displayColumns = ["Result"];
        private Dictionary<string, JsonElement>? _selectedDocument;

        private string _selectedDocumentId = string.Empty;
        private string _documentJson = "{\n  \"Id\": \"\"\n}";

        private bool _busy;
        private bool _savingProfile;
        private bool _connectingProfile;
        private bool _showProfileManagement = true;
        private bool _showProfileModal;
        private bool _editingProfile;
        private bool _showDocumentEditorModal;
        private bool _showDeleteConfirmModal;
        private bool _showRowContextMenu;
        private double _rowContextMenuX;
        private double _rowContextMenuY;
        private Dictionary<string, JsonElement>? _rowContextMenuDocument;
        private string _pendingDeleteDocumentId = string.Empty;
        private string? _errorMessage;
        private string? _infoMessage;

        private ConnectionProfile? ActiveProfile => _activeProfileId is null ? null : _profiles.FirstOrDefault(x => x.Id == _activeProfileId.Value);

        private ConnectionProfile? SelectedProfile => _selectedProfileId is null ? null : _profiles.FirstOrDefault(x => x.Id == _selectedProfileId.Value);

        private IReadOnlyList<ConnectionProfile> OrderedProfiles => _profiles.OrderByDescending(x => x.UpdatedUtc).ThenBy(x => GetProfileDisplayName(x), StringComparer.OrdinalIgnoreCase).ToList();

        private bool ShowProfileManagement => _showProfileManagement || ActiveProfile is null;

        private bool IsProfileActionBusy => _savingProfile || _connectingProfile;
        private bool SelectedCollectionIsSystem => IsReservedCollection(_selectedCollection);
        private string ProfileModalTitle => _editingProfile ? "Edit Profile" : "Add Profile";

        private string ActiveProfileSummary => ActiveProfile is null ? "Not connected. Open profile management to connect." : $"Connected to {ActiveProfile.Database} at {GetProfileDisplayName(ActiveProfile)}";

        private IReadOnlyList<string> DisplayColumns => _displayColumns;

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

                _profiles = NormalizeProfiles(loaded);
                Guid? savedActiveProfileId = await ProfileStore.LoadActiveProfileIdAsync().ConfigureAwait(false);
                _activeProfileId = null;
                _showProfileManagement = true;
                _showProfileModal = false;

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
            OpenCreateProfileModal();
        }

        private void OpenProfileManagement()
        {
            _showProfileManagement = true;
            _showTableModal = false;
            ClearMessages();
            CloseDocumentEditorModal();
            CancelDeleteConfirmation();
            DismissRowContextMenu();
        }

        private async Task DisconnectAsync()
        {
            _activeProfileId = null;
            _showProfileManagement = true;
            _overview = null;
            _discoveredCollections = [];
            _collections = [];
            _selectedCollection = null;
            ClearDocuments();
            _selectedDocument = null;
            _selectedDocumentId = string.Empty;
            _newCollectionName = string.Empty;
            _showTableModal = false;
            CreateDocumentTemplate();
            CloseDocumentEditorModal();
            CancelDeleteConfirmation();
            DismissRowContextMenu();
            await ProfileStore.SaveActiveProfileIdAsync(null).ConfigureAwait(false);
            _infoMessage = "Disconnected. Session ended.";
            _errorMessage = null;
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
            await SaveProfileInternalAsync(connectAfterSave: false).ConfigureAwait(false);
        }

        private async Task DeleteProfileAsync(Guid? profileId = null)
        {
            Guid? targetProfileId = profileId ?? _selectedProfileId;
            if (targetProfileId is null)
            {
                return;
            }

            ConnectionProfile? deletedProfile = _profiles.FirstOrDefault(x => x.Id == targetProfileId.Value);
            int removed = _profiles.RemoveAll(x => x.Id == targetProfileId.Value);
            if (removed == 0)
            {
                return;
            }

            if (_activeProfileId == targetProfileId)
            {
                _activeProfileId = null;
                _showProfileManagement = true;
                _overview = null;
                _discoveredCollections = [];
                _collections = [];
                _selectedCollection = null;
                _newCollectionName = string.Empty;
                _showTableModal = false;
                ClearDocuments();
                _selectedDocument = null;
                _selectedDocumentId = string.Empty;
                CreateDocumentTemplate();
                CloseDocumentEditorModal();
                CancelDeleteConfirmation();
                DismissRowContextMenu();
                await ProfileStore.SaveActiveProfileIdAsync(null).ConfigureAwait(false);
            }

            if (_selectedProfileId == targetProfileId)
            {
                _selectedProfileId = _profiles.FirstOrDefault()?.Id;
            }

            _editor = _selectedProfileId is Guid nextId
                ? _profiles.First(x => x.Id == nextId).Clone()
                : ConnectionProfile.CreateDefault();

            await PersistProfilesAsync().ConfigureAwait(false);

            _errorMessage = null;
            _infoMessage = $"Profile '{GetProfileDisplayName(deletedProfile)}' deleted.";
        }

        private async Task ConnectUsingEditorAsync()
        {
            await SaveProfileInternalAsync(connectAfterSave: true).ConfigureAwait(false);
        }

        private void OpenCreateProfileModal()
        {
            _editingProfile = false;
            _selectedProfileId = null;
            _editor = ConnectionProfile.CreateDefault();
            _showProfileModal = true;
            ClearMessages();
        }

        private void OpenEditProfileModal(Guid profileId)
        {
            ConnectionProfile? profile = _profiles.FirstOrDefault(x => x.Id == profileId);
            if (profile is null)
            {
                return;
            }

            _editingProfile = true;
            _selectedProfileId = profile.Id;
            _editor = profile.Clone();
            _showProfileModal = true;
            ClearMessages();
        }

        private void CloseProfileModal()
        {
            _showProfileModal = false;
            _editingProfile = false;
        }

        private async Task SaveProfileFromModalAsync()
        {
            await SaveProfileInternalAsync(connectAfterSave: false).ConfigureAwait(false);
        }

        private async Task SaveAndConnectProfileFromModalAsync()
        {
            await SaveProfileInternalAsync(connectAfterSave: true).ConfigureAwait(false);
        }

        private async Task ConnectProfileAsync(Guid profileId)
        {
            ConnectionProfile? profile = _profiles.FirstOrDefault(x => x.Id == profileId);
            if (profile is null)
            {
                return;
            }

            _selectedProfileId = profile.Id;
            _editor = profile.Clone();

            _connectingProfile = true;
            try
            {
                bool connected = await ConnectAsync(profile, quiet: false).ConfigureAwait(false);
                _showProfileManagement = !connected;
            }
            finally
            {
                _connectingProfile = false;
            }
        }

        private async Task SaveProfileInternalAsync(bool connectAfterSave)
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

                _showProfileModal = false;
                _editingProfile = false;
                _infoMessage = $"Profile '{GetProfileDisplayName(normalized)}' saved.";
                _errorMessage = null;
            }
            finally
            {
                _savingProfile = false;
            }

            if (!connectAfterSave)
            {
                return;
            }

            _connectingProfile = true;
            try
            {
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
                List<string> discoveredCollections = collectionsResult.Data ?? [];
                _discoveredCollections = NormalizeCollectionNames(discoveredCollections);
                RebuildVisibleCollections();

                if (_collections.Count == 0)
                {
                    ClearDocuments();
                    _selectedDocument = null;
                    _selectedCollection = null;

                    if (!quiet)
                    {
                        bool reservedCollectionsOnly = discoveredCollections.Any(IsReservedCollection);
                        _infoMessage = reservedCollectionsOnly && !_includeSystemCollections
                            ? "Connected. Only reserved collections are present. Turn on 'Show System Tables' to inspect them."
                            : reservedCollectionsOnly
                            ? "Connected. Only reserved collections are present. Create a new table to begin."
                            : "Connected. No tables discovered yet for this database. Create one to begin.";
                    }

                    return true;
                }

                if (string.IsNullOrWhiteSpace(_selectedCollection)
                    || !_collections.Contains(_selectedCollection, StringComparer.OrdinalIgnoreCase))
                {
                    _selectedCollection = _collections[0];
                }

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

        private async Task SelectCollectionAsync(string collection)
        {
            if (string.IsNullOrWhiteSpace(collection))
            {
                return;
            }

            _selectedCollection = collection.Trim();
            UseCollectionTemplate();
            await BrowseCollectionAsync().ConfigureAwait(false);
        }

        private async Task OnSystemCollectionsChangedAsync(ChangeEventArgs args)
        {
            bool includeSystemCollections = false;
            if (args.Value is bool typedValue)
            {
                includeSystemCollections = typedValue;
            }
            else if (args.Value is string rawValue)
            {
                includeSystemCollections = string.Equals(rawValue, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rawValue, "on", StringComparison.OrdinalIgnoreCase);
            }

            _includeSystemCollections = includeSystemCollections;
            RebuildVisibleCollections();

            if (_collections.Count == 0)
            {
                _selectedCollection = null;
                ClearDocuments();
                _selectedDocument = null;
                CreateDocumentTemplate();
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedCollection)
                || !_collections.Contains(_selectedCollection, StringComparer.OrdinalIgnoreCase))
            {
                _selectedCollection = _collections[0];
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

            if (IsReservedCollection(_selectedCollection))
            {
                if (!_includeSystemCollections)
                {
                    _errorMessage = $"Collection '{_selectedCollection}' is reserved. Use '/api/cache' endpoints.";
                    _infoMessage = null;
                    ClearDocuments();
                    return;
                }

                await BrowseSystemCollectionAsync(profile).ConfigureAwait(false);
                return;
            }

            _busy = true;
            ClearMessages();

            try
            {
                ApiResult<List<Dictionary<string, JsonElement>>> result = await ApiClient.ListDocumentsAsync(profile, _selectedCollection, 0, DefaultBrowseTake).ConfigureAwait(false);

                if (!result.Success)
                {
                    _errorMessage = result.ErrorMessage;
                    ClearDocuments();
                    return;
                }

                SetDocuments(result.Data ?? []);

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

        private async Task BrowseSystemCollectionAsync(ConnectionProfile profile)
        {
            _busy = true;
            ClearMessages();

            try
            {
                int safeTake = Math.Clamp(DefaultBrowseTake, 1, 10_000);
                string query = $"SELECT $ FROM {_selectedCollection} LIMIT {safeTake}";
                ApiResult<QueryResponseDto> result = await ApiClient.ExecuteQueryAsync(profile, query, safeTake).ConfigureAwait(false);

                if (!result.Success)
                {
                    _errorMessage = result.ErrorMessage;
                    ClearDocuments();
                    return;
                }

                SetDocuments(result.Data?.Rows ?? []);

                if (_documents.Count == 0)
                {
                    _selectedDocument = null;
                    CreateDocumentTemplate();
                    _infoMessage = $"System table '{_selectedCollection}' returned no rows.";
                    return;
                }

                if (!TrySelectDocumentById(_selectedDocumentId))
                {
                    SelectDocument(_documents[0]);
                }

                _infoMessage = $"Loaded {_documents.Count} row(s) from system table '{_selectedCollection}'.";
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task CreateCollectionAsync()
        {
            ConnectionProfile? profile = ActiveProfile;
            if (profile is null)
            {
                _errorMessage = "Connect a profile first.";
                _infoMessage = null;
                return;
            }

            string collectionName = (_newCollectionName ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                _errorMessage = "Table name is required.";
                _infoMessage = null;
                return;
            }

            if (!DatabaseNamePattern.IsMatch(collectionName))
            {
                _errorMessage = "Table name can only include lowercase letters, digits, '-' or '_' (max 63 chars).";
                _infoMessage = null;
                return;
            }

            if (IsReservedCollection(collectionName))
            {
                _errorMessage = $"Table '{collectionName}' is reserved. Choose another name.";
                _infoMessage = null;
                return;
            }

            _creatingCollection = true;
            _busy = true;
            ClearMessages();

            try
            {
                ApiResult<JsonElement> result = await ApiClient.RegisterCollectionAsync(profile, collectionName).ConfigureAwait(false);
                if (!result.Success)
                {
                    _errorMessage = result.ErrorMessage;
                    return;
                }

                if (!_collections.Contains(collectionName, StringComparer.OrdinalIgnoreCase))
                {
                    _discoveredCollections.Add(collectionName);
                    _discoveredCollections = NormalizeCollectionNames(_discoveredCollections);
                    RebuildVisibleCollections();
                }

                _selectedCollection = _collections.First(x => string.Equals(x, collectionName, StringComparison.OrdinalIgnoreCase));
                _newCollectionName = string.Empty;
                UseCollectionTemplate();
                await BrowseCollectionAsync().ConfigureAwait(false);
                _infoMessage = $"Table '{collectionName}' registered.";
            }
            finally
            {
                _creatingCollection = false;
                _busy = false;
            }
        }

        private void OpenTableModal()
        {
            _showTableModal = true;
            _newCollectionName = string.Empty;
            ClearMessages();
        }

        private void CloseTableModal()
        {
            _showTableModal = false;
        }

        private async Task CreateCollectionFromModalAsync()
        {
            await CreateCollectionAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(_errorMessage))
            {
                _showTableModal = false;
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

            if (!TryValidateQuerySafety(_queryText, out string? safetyError))
            {
                _errorMessage = safetyError;
                _infoMessage = null;
                return;
            }

            _busy = true;
            ClearMessages();

            try
            {
                ApiResult<QueryResponseDto> result = await ApiClient.ExecuteQueryAsync(profile, _queryText, 10_000).ConfigureAwait(false);

                if (!result.Success)
                {
                    _errorMessage = result.ErrorMessage;
                    ClearDocuments();
                    return;
                }

                List<Dictionary<string, JsonElement>> rows = result.Data?.Rows ?? [];
                SetDocuments(rows);

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

            _queryText = $"SELECT $ FROM {_selectedCollection} LIMIT {DefaultBrowseTake}";
        }

        private void SetDocuments(List<Dictionary<string, JsonElement>> documents)
        {
            _documents = documents ?? [];
            UpdateDisplayColumns();
        }

        private void ClearDocuments()
        {
            _documents = [];
            _displayColumns = ["Result"];
        }

        private void UpdateDisplayColumns()
        {
            if (_documents.Count == 0)
            {
                _displayColumns = ["Result"];
                return;
            }

            // Precompute visible columns once per dataset to keep render-time work minimal.
            List<string> columns = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            if (_documents.Any(x => x.Keys.Any(key => string.Equals(key, "Id", StringComparison.OrdinalIgnoreCase))))
            {
                columns.Add("Id");
                seen.Add("Id");
            }

            foreach (Dictionary<string, JsonElement> document in _documents)
            {
                foreach (string key in document.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (seen.Contains(key))
                    {
                        continue;
                    }

                    columns.Add(key);
                    seen.Add(key);
                }
            }

            _displayColumns = columns.Count == 0 ? ["Result"] : columns;
        }

        private static bool TryValidateQuerySafety(string queryText, out string? error)
        {
            string normalized = (queryText ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                error = "Enter a query first.";
                return false;
            }

            if (!normalized.StartsWith("select", StringComparison.OrdinalIgnoreCase))
            {
                error = null;
                return true;
            }

            bool hasWhere = normalized.Contains(" where ", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\nwhere ", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\twhere ", StringComparison.OrdinalIgnoreCase);
            bool hasLimit = normalized.Contains(" limit ", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(" limit", StringComparison.OrdinalIgnoreCase);

            if (!hasWhere && !hasLimit)
            {
                error = "SELECT queries without a WHERE clause must include LIMIT.";
                return false;
            }

            error = null;
            return true;
        }

        private void SelectDocument(Dictionary<string, JsonElement> document)
        {
            _selectedDocument = document;
            _documentJson = JsonSerializer.Serialize(document, PrettyJsonOptions);

            _selectedDocumentId = ExtractId(document) ?? string.Empty;
        }

        private void OpenDocumentEditorModal(Dictionary<string, JsonElement> document)
        {
            SelectDocument(document);
            _showDocumentEditorModal = true;
            DismissRowContextMenu();
        }

        private void OpenNewDocumentModal()
        {
            CreateDocumentTemplate();
            _showDocumentEditorModal = true;
            DismissRowContextMenu();
        }

        private void CloseDocumentEditorModal()
        {
            _showDocumentEditorModal = false;
        }

        private void ShowRowContextMenu(MouseEventArgs args, Dictionary<string, JsonElement> document)
        {
            SelectDocument(document);
            _rowContextMenuDocument = document;
            _rowContextMenuX = args.ClientX;
            _rowContextMenuY = args.ClientY;
            _showRowContextMenu = true;
        }

        private void DismissRowContextMenu()
        {
            _showRowContextMenu = false;
            _rowContextMenuDocument = null;
        }

        private Task EditFromRowContextMenuAsync()
        {
            Dictionary<string, JsonElement>? document = _rowContextMenuDocument;
            DismissRowContextMenu();

            if (document is null)
            {
                return Task.CompletedTask;
            }

            OpenDocumentEditorModal(document);
            return Task.CompletedTask;
        }

        private Task DeleteFromRowContextMenuAsync()
        {
            Dictionary<string, JsonElement>? document = _rowContextMenuDocument;
            DismissRowContextMenu();

            if (document is null)
            {
                return Task.CompletedTask;
            }

            OpenDeleteConfirmationForDocument(document);
            return Task.CompletedTask;
        }

        private void OpenDeleteConfirmationForDocument(Dictionary<string, JsonElement> document)
        {
            SelectDocument(document);
            _pendingDeleteDocumentId = _selectedDocumentId.Trim();

            if (string.IsNullOrWhiteSpace(_pendingDeleteDocumentId))
            {
                _errorMessage = "Select a document with a valid Id before deleting.";
                _infoMessage = null;
                return;
            }

            _showDeleteConfirmModal = true;
        }

        private void CancelDeleteConfirmation()
        {
            _showDeleteConfirmModal = false;
            _pendingDeleteDocumentId = string.Empty;
        }

        private async Task ConfirmDeleteFromModalAsync()
        {
            if (string.IsNullOrWhiteSpace(_pendingDeleteDocumentId))
            {
                CancelDeleteConfirmation();
                return;
            }

            _selectedDocumentId = _pendingDeleteDocumentId;
            CancelDeleteConfirmation();
            await DeleteDocumentAsync().ConfigureAwait(false);
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

            if (SelectedCollectionIsSystem)
            {
                _errorMessage = "System tables are read-only in Studio. Save is disabled for this table.";
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

            if (SelectedCollectionIsSystem)
            {
                _errorMessage = "System tables are read-only in Studio. Delete is disabled for this table.";
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
                if (!string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase))
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

        private static bool IsReservedCollection(string? collectionName)
        {
            return !string.IsNullOrWhiteSpace(collectionName)
                && ReservedCollections.Contains(collectionName.Trim());
        }

        private static List<string> FilterBrowsableCollections(IEnumerable<string> collections)
        {
            return collections
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Where(x => !IsReservedCollection(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> NormalizeCollectionNames(IEnumerable<string> collections)
        {
            return collections
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void RebuildVisibleCollections()
        {
            _collections = _includeSystemCollections
                ? NormalizeCollectionNames(_discoveredCollections)
                : FilterBrowsableCollections(_discoveredCollections);
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

        private static string GetProfileDisplayName(ConnectionProfile? profile)
        {
            if (profile is null)
            {
                return "-";
            }

            string explicitName = (profile.Name ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                return explicitName;
            }

            return BuildServerEndpointLabel(profile.BaseUrl);
        }

        private static string BuildServerEndpointLabel(string? baseUrl)
        {
            string normalizedUrl = (baseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedUrl))
            {
                return "unnamed-endpoint";
            }

            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? serverUri))
            {
                return normalizedUrl;
            }

            return serverUri.IsDefaultPort
                ? $"{serverUri.Scheme}://{serverUri.Host}"
                : $"{serverUri.Scheme}://{serverUri.Host}:{serverUri.Port}";
        }

        private static string BuildServerUniqueKey(string? baseUrl)
        {
            string normalizedUrl = (baseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedUrl))
            {
                return string.Empty;
            }

            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? serverUri))
            {
                return normalizedUrl.ToLowerInvariant();
            }

            string scheme = serverUri.Scheme.ToLowerInvariant();
            string host = serverUri.Host.ToLowerInvariant();
            int port = serverUri.Port;

            return $"{scheme}://{host}:{port}";
        }

        private static List<ConnectionProfile> NormalizeProfiles(IEnumerable<ConnectionProfile> profiles)
        {
            List<ConnectionProfile> orderedProfiles = profiles
                .Where(x => x is not null)
                .OrderByDescending(x => x.UpdatedUtc)
                .ToList();

            Dictionary<string, ConnectionProfile> uniqueProfiles = new(StringComparer.OrdinalIgnoreCase);
            List<ConnectionProfile> results = new List<ConnectionProfile>();

            foreach (ConnectionProfile profile in orderedProfiles)
            {
                string key = BuildServerUniqueKey(profile.BaseUrl);
                if (uniqueProfiles.ContainsKey(key))
                {
                    continue;
                }

                ConnectionProfile normalized = profile.Clone();
                normalized.Name = (normalized.Name ?? string.Empty).Trim();
                normalized.BaseUrl = BuildServerEndpointLabel(normalized.BaseUrl);
                normalized.Database = (normalized.Database ?? string.Empty).Trim().ToLowerInvariant();
                normalized.Credential = (normalized.Credential ?? string.Empty).Trim();
                if (normalized.UpdatedUtc == default)
                {
                    normalized.UpdatedUtc = DateTime.UtcNow;
                }

                uniqueProfiles[key] = normalized;
                results.Add(normalized);
            }

            return results.OrderByDescending(x => x.UpdatedUtc).ToList();
        }

        private void UpsertProfile(ConnectionProfile profile)
        {
            string targetKey = BuildServerUniqueKey(profile.BaseUrl);
            int sameEndpointIndex = _profiles.FindIndex(x => string.Equals(BuildServerUniqueKey(x.BaseUrl), targetKey, StringComparison.OrdinalIgnoreCase));
            int sameIdIndex = _profiles.FindIndex(x => x.Id == profile.Id);

            if (sameEndpointIndex >= 0)
            {
                Guid existingId = _profiles[sameEndpointIndex].Id;
                profile.Id = existingId;
                _profiles[sameEndpointIndex] = profile;

                if (sameIdIndex >= 0 && sameIdIndex != sameEndpointIndex)
                {
                    _profiles.RemoveAt(sameIdIndex);
                }

                return;
            }

            if (sameIdIndex >= 0)
            {
                _profiles[sameIdIndex] = profile;
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

            profile = input.Clone();
            profile.Name = name;
            profile.BaseUrl = BuildServerEndpointLabel(serverUri.ToString());
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
