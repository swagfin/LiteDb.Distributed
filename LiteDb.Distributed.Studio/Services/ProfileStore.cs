using LiteDb.Distributed.Studio.Models;

namespace LiteDb.Distributed.Studio.Services;

public sealed class ProfileStore(BrowserStorageService storage)
{
    private const string ProfilesKey = "litedb.distributed.studio.profiles.v1";
    private const string ActiveProfileIdKey = "litedb.distributed.studio.active-profile-id.v1";

    public async Task<IReadOnlyList<ConnectionProfile>> LoadProfilesAsync()
    {
        var profiles = await storage.GetAsync<List<ConnectionProfile>>(ProfilesKey).ConfigureAwait(false);

        return profiles ?? [];
    }

    public Task SaveProfilesAsync(IReadOnlyList<ConnectionProfile> profiles)
    {
        return storage.SetAsync(ProfilesKey, profiles);
    }

    public Task<Guid?> LoadActiveProfileIdAsync()
    {
        return storage.GetAsync<Guid?>(ActiveProfileIdKey);
    }

    public Task SaveActiveProfileIdAsync(Guid? profileId)
    {
        if (profileId is null)
        {
            return storage.RemoveAsync(ActiveProfileIdKey);
        }

        return storage.SetAsync(ActiveProfileIdKey, profileId.Value);
    }
}
