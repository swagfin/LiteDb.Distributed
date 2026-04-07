using LiteDb.Distributed.Studio.Models;

namespace LiteDb.Distributed.Studio.Services;

public class ProfileStore
{
    private const string ProfilesKey = "litedb.distributed.studio.profiles.v1";
    private const string ActiveProfileIdKey = "litedb.distributed.studio.active-profile-id.v1";
    private readonly BrowserStorageService _storage;

    public ProfileStore(BrowserStorageService storage)
    {
        _storage = storage;
    }

    public async Task<IReadOnlyList<ConnectionProfile>> LoadProfilesAsync()
    {
        List<ConnectionProfile>? profiles = await _storage.GetAsync<List<ConnectionProfile>>(ProfilesKey).ConfigureAwait(false);

        return profiles ?? [];
    }

    public Task SaveProfilesAsync(IReadOnlyList<ConnectionProfile> profiles)
    {
        return _storage.SetAsync(ProfilesKey, profiles);
    }

    public Task<Guid?> LoadActiveProfileIdAsync()
    {
        return _storage.GetAsync<Guid?>(ActiveProfileIdKey);
    }

    public Task SaveActiveProfileIdAsync(Guid? profileId)
    {
        if (profileId is null)
        {
            return _storage.RemoveAsync(ActiveProfileIdKey);
        }

        return _storage.SetAsync(ActiveProfileIdKey, profileId.Value);
    }
}
